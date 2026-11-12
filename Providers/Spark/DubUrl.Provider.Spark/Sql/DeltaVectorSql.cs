using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Querying.Dialects;

namespace DubUrl.Provider.Spark.Sql;

internal static class DeltaVectorSql
{
    internal const string ConstraintPrefix = "duburl_vector_v1_";
    private const string Marker = "/*duburl_vector_v1:";

    // Delta installs CHECK constraints with ALTER TABLE. Carry the dimension through the
    // shared CREATE TABLE template in a SQL comment, then emit the constraint in the command plan.
    internal static string Type(int dimensions)
        => FormattableString.Invariant($"ARRAY<FLOAT> {Marker}{dimensions}*/");

    internal static IEnumerable<SchemaSqlCommand> Plan(SchemaSqlCommand statement)
    {
        var sql = statement.CommandText;
        if (statement.Operation != SchemaSqlOperation.CreateTable || !sql.Contains(Marker, StringComparison.Ordinal)
            || !sql.TrimStart().StartsWith("CREATE TABLE ", StringComparison.Ordinal))
        {
            yield return statement;
            yield break;
        }

        var vectors = new List<(string Column, int Dimensions)>();
        foreach (var (column, definition) in SparkCatalogDefinition.Columns(sql))
        {
            var tokens = SparkSqlSyntax.Tokenize(definition);
            var end = SparkCatalogDefinition.AfterType(tokens);
            if (end < tokens.Count && TryDimension(tokens[end].Text, out var dimensions))
                vectors.Add((column, dimensions));
        }
        if (vectors.Count == 0)
        {
            yield return statement;
            yield break;
        }

        var sqlTokens = SparkSqlSyntax.Tokenize(sql);
        var open = sqlTokens.Find(token => token.Depth == 0 && token.Is("("));
        var target = sql[sqlTokens[2].Start..open.Start].TrimEnd();
        foreach (var token in sqlTokens.Where(token => token.Depth == 1 && TryDimension(token.Text, out _)).Reverse())
            sql = sql.Remove(token.Start - 1, token.End - token.Start + 1);
        yield return statement with { CommandText = sql };

        foreach (var (column, dimensions) in vectors)
        {
            var check = $"ALTER TABLE {target} ADD CONSTRAINT `{ConstraintName(column)}` CHECK ({Predicate(column, dimensions)});";
            yield return statement with { CommandText = check };
        }
    }

    internal static string ConstraintName(string column)
        => ConstraintPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(column.ToLowerInvariant())))[..20].ToLowerInvariant();

    internal static int? ReadDimensions(string column, string? check)
    {
        if (check is null) return null;

        var tokens = PredicateTokens(check);
        if (tokens.Count < 2 || !TryPositiveInteger(tokens[^2].Text, out var dimensions))
            return null;

        var expected = PredicateTokens(Predicate(column, dimensions));
        return tokens.Select(token => token.Text).SequenceEqual(expected.Select(token => token.Text), StringComparer.OrdinalIgnoreCase)
            ? dimensions : null;
    }

    private static List<SparkSqlSyntax.Token> PredicateTokens(string sql)
    {
        var tokens = SparkSqlSyntax.Tokenize(sql);
        while (tokens.Count > 1 && tokens[0].Is("(") && tokens[^1].Is(")")
            && tokens.Skip(1).Take(tokens.Count - 2).All(token => token.Depth > tokens[0].Depth))
            tokens = tokens.GetRange(1, tokens.Count - 2);
        return tokens;
    }

    private static string Predicate(string column, int dimensions)
    {
        var name = new SparkIdentifierFormatter().Format(column);
        return FormattableString.Invariant($"({name} IS NULL OR (size(array_compact({name})) = size({name}) AND size({name}) = {dimensions}))");
    }

    private static bool TryDimension(string token, out int dimensions)
    {
        dimensions = 0;
        return token.StartsWith(Marker, StringComparison.Ordinal) && token.EndsWith("*/", StringComparison.Ordinal)
            && TryPositiveInteger(token[Marker.Length..^2], out dimensions);
    }

    private static bool TryPositiveInteger(string text, out int number)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
}
