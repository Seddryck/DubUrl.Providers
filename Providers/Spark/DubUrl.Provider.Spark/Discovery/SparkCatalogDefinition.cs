using System.Globalization;
using System.Text;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;

namespace DubUrl.Provider.Spark.Discovery;

internal static class SparkCatalogDefinition
{
    internal static Dictionary<string, string> Columns(string sql)
    {
        var tokens = SparkSqlSyntax.Tokenize(sql);
        var start = tokens.FindIndex(token => token.Depth == 0 && token.Is("("));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (start < 0) return result;
        var first = start + 1;
        for (var i = first; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var end = token.Depth == 0 && token.Is(")");
            if (!end && !(token.Depth == 1 && token.Is(","))) continue;
            if (first < i && !tokens[first].Is("CONSTRAINT") && !tokens[first].Is("PRIMARY") && !tokens[first].Is("UNIQUE"))
                result[Identifier(tokens[first].Text)] = sql[tokens[first].Start..token.Start].Trim();
            if (end) break;
            first = i + 1;
        }
        return result;
    }

    internal static string? DefaultSql(string definition)
    {
        var tokens = SparkSqlSyntax.Tokenize(definition);
        var index = tokens.FindIndex(token => token.Depth == 0 && token.Is("DEFAULT"));
        if (index < 1 || tokens[index - 1].Is("BY") || index == tokens.Count - 1) return null;
        var end = tokens.FindIndex(index + 2, token => token.Depth == 0 &&
            (token.Is("COMMENT") || token.Is("CONSTRAINT") || token.Is("NOT") || token.Is("MASK")));
        return definition[tokens[index + 1].Start..(end < 0 ? definition.Length : tokens[end].Start)].Trim();
    }

    internal static MetadataGeneration? Generation(string definition, out bool unsupported)
    {
        unsupported = false;
        var tokens = SparkSqlSyntax.Tokenize(definition);
        var generated = tokens.FindIndex(token => token.Depth == 0 && token.Is("GENERATED"));
        if (generated < 0) return null;
        if (tokens.Skip(generated + 1).Take(4).Select(token => token.Text.ToUpperInvariant())
            .SequenceEqual(new[] { "BY", "DEFAULT", "AS", "IDENTITY" }))
        {
            var seed = IdentityNumber(tokens, "START", "WITH");
            var increment = IdentityNumber(tokens, "INCREMENT", "BY");
            if (seed is not null && increment is not null && increment != 0)
                return new(seed, increment);
        }
        else if (tokens.Count > generated + 4 && tokens[generated + 1].Is("ALWAYS")
            && tokens[generated + 2].Is("AS") && tokens[generated + 3].Is("("))
        {
            var end = tokens.FindIndex(generated + 4, token => token.Depth == 0 && token.Is(")"));
            if (end > generated + 4 && TryExpression(definition[tokens[generated + 3].End..tokens[end].Start], out var expression))
                return new(Expression: expression, Stored: true);
        }
        unsupported = true;
        return null;
    }

    private static long? IdentityNumber(List<SparkSqlSyntax.Token> tokens, string first, string second)
    {
        var index = tokens.FindIndex(token => token.Depth == 1 && token.Is(first));
        if (index < 0) return 1;
        if (tokens.Count < index + 3 || !tokens[index + 1].Is(second)) return null;
        var value = tokens[index + 2].Text;
        if (value == "-" && tokens.Count > index + 3) value += tokens[index + 3].Text;
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    internal static ColumnDefaultExpression? SymbolicDefault(string sql)
    {
        var normalized = Normalize(sql);
        return normalized switch
        {
            "CURRENT_DATE" or "CURRENT_DATE()" => ColumnDefaultExpression.CurrentDate,
            "CURRENT_TIMESTAMP" or "CURRENT_TIMESTAMP()" or "CAST(CURRENT_TIMESTAMP()ASTIMESTAMP_NTZ)" =>
                ColumnDefaultExpression.CurrentTimestamp(),
            "CURRENT_TIME" or "CURRENT_TIME()" => ColumnDefaultExpression.CurrentTime(),
            "CURRENT_USER" or "CURRENT_USER()" => ColumnDefaultExpression.CurrentUser,
            "SESSION_USER" or "SESSION_USER()" => ColumnDefaultExpression.SessionUser,
            "CURRENT_SCHEMA" or "CURRENT_SCHEMA()" => ColumnDefaultExpression.CurrentSchema,
            "CURRENT_DATABASE" or "CURRENT_DATABASE()" or "CURRENT_CATALOG" or "CURRENT_CATALOG()" =>
                ColumnDefaultExpression.CurrentDatabase,
            _ when normalized == Normalize(SparkColumnDefaults.UtcTimestamp) => ColumnDefaultExpression.CurrentUtcTimestamp(),
            _ => null,
        };
    }

    private static string Normalize(string sql)
        => string.Concat(SparkSqlSyntax.Tokenize(sql).Select(token => token.Text)).ToUpperInvariant();

    internal static MetadataView View(MetadataName name, MetadataColumn[] columns, string? sql)
    {
        var inventory = new MetadataView(name, columns);
        if (sql is null) return inventory;
        var tokens = SparkSqlSyntax.Tokenize(sql);
        var select = tokens.FindIndex(token => token.Depth == 0 && token.Is("SELECT"));
        if (select < 0 || (select != 0 && !tokens[0].Is("CREATE"))) return inventory;
        var from = tokens.FindIndex(select + 1, token => token.Depth == 0 && token.Is("FROM"));
        if (from <= select + 1 || from == tokens.Count - 1) return inventory;
        MetadataName source;
        try
        {
            var parsed = MetadataNames.Parse(sql[tokens[from + 1].Start..].Trim().TrimEnd(';').Trim());
            if (parsed.Catalog is not null) return inventory;
            source = new(parsed.Name, parsed.Schema ?? name.Schema, Catalog: parsed.Database ?? name.Catalog);
        }
        catch (ArgumentException) { return inventory; }

        var projections = new List<MetadataProjection>();
        var start = select + 1;
        for (var i = start; i <= from; i++)
        {
            if (i != from && !(tokens[i].Depth == 0 && tokens[i].Is(","))) continue;
            if (i == start) return inventory;
            var asIndex = tokens.FindIndex(start, i - start, token => token.Depth == 0 && token.Is("AS"));
            if (asIndex != -1 && asIndex != i - 2) return inventory;
            var expressionSql = sql[tokens[start].Start..tokens[asIndex < 0 ? i : asIndex].Start].Trim();
            if (!TryExpression(expressionSql, out var expression)) return inventory;
            string? alias = null;
            if (asIndex >= 0)
            {
                try { alias = Identifier(tokens[asIndex + 1].Text); }
                catch (ArgumentException) { return inventory; }
            }
            projections.Add(new(expression!, alias));
            start = i + 1;
        }
        return new(name, columns, source, projections);
    }

    internal static bool TryExpression(string sql, out MetadataExpression? expression)
    {
        if (TryString(sql, out var text))
        {
            expression = new("value", Value: text);
            return true;
        }
        var tokens = SparkSqlSyntax.Tokenize(sql);
        if (tokens.Count >= 4 && tokens[1].Is("(") && tokens[^1].Is(")") && tokens[^1].Depth == 0
            && tokens[0].Text.ToUpperInvariant() is "LOWER" or "UPPER" or "LENGTH"
            && TryExpression(sql[tokens[1].End..tokens[^1].Start], out var argument))
        {
            var function = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tokens[0].Text.ToLowerInvariant());
            expression = new("function", function, Arguments: [argument!]);
            return true;
        }
        return DiscoveryExpressions.TryExpression(sql, out expression);
    }

    internal static bool TryString(string sql, out string? value)
    {
        value = null;
        var tokens = SparkSqlSyntax.Tokenize(sql);
        if (tokens.Count != 1 || tokens[0].Text is not ['\'', .., '\'']) return false;
        var text = tokens[0].Text;
        var result = new StringBuilder();
        for (var i = 1; i < text.Length - 1; i++)
        {
            var character = text[i];
            if (character == '\'' && text[i + 1] == '\'') i++;
            else if (character == '\\' && i + 1 < text.Length - 1)
            {
                character = text[++i] switch
                {
                    '0' => '\0', 'r' => '\r', 'n' => '\n', 't' => '\t',
                    '\\' => '\\', '\'' => '\'', '"' => '"', _ => '\0',
                };
                if (character == '\0' && text[i] != '0') return false;
            }
            result.Append(character);
        }
        value = result.ToString();
        return true;
    }

    private static string Identifier(string sql)
    {
        var name = MetadataNames.Parse(sql);
        if (name.Schema is not null) throw new ArgumentException("A literal identifier was expected.");
        return name.Name;
    }
}
