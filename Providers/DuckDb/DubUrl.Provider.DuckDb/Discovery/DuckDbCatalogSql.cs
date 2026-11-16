using System.Text;
using System.Data;
using System.Globalization;
using DubUrl.Discovery;
using DubUrl.Provider.DuckDb.Schema.Generation;
using DubUrl.Schema;

namespace DubUrl.Provider.DuckDb.Discovery;

/// <summary>Reads the small canonical DDL fragments absent from DuckDB's structured catalogs. Never executes catalog SQL.</summary>
internal static class DuckDbCatalogSql
{
    private sealed record Token(string Value, int Start, int End, bool Quoted = false, bool Literal = false)
    {
        public bool Is(string word) => !Quoted && !Literal && Value.Equals(word, StringComparison.OrdinalIgnoreCase);
        public bool Identifier => !Literal && (Quoted || (Value.Length > 0 && (char.IsLetter(Value[0]) || Value[0] == '_')));
    }

    private static readonly IReadOnlyDictionary<string, DbType> StructuredScalars = DuckDbStructuredTypes.Scalars
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    internal static VectorType? FixedVector(string? sql)
    {
        if (sql is null || !sql.EndsWith(']') || sql.EndsWith("[]", StringComparison.Ordinal))
            return null;
        var tokens = Tokenize(sql);
        return tokens.Count == 4 && !tokens[0].Quoted && !tokens[0].Literal
            && StructuredScalars.TryGetValue(tokens[0].Value, out var element) && DuckDbStructuredTypes.IsVectorElement(element)
            && tokens[1].Is("[") && tokens[3].Is("]") && ReadDimension(tokens[2]) is { } dimensions
            ? new(element, dimensions) : null;
    }

    internal static bool IsVectorCheck(string sql, string column, int dimensions)
    {
        var tokens = Tokenize(sql);
        var start = 0;
        var end = tokens.Count;
        while (end - start > 1 && tokens[start].Is("(") && Closing(tokens, start) == end - 1)
        {
            start++;
            end--;
        }

        // Require the built-in macro's qualified name. An unqualified macro can be shadowed.
        return end - start == 10 && SameIdentifier(tokens[start], "system") && tokens[start + 1].Is(".")
            && SameIdentifier(tokens[start + 2], "main") && tokens[start + 3].Is(".")
            && SameIdentifier(tokens[start + 4], "list_count") && tokens[start + 5].Is("(")
            && SameIdentifier(tokens[start + 6], column) && tokens[start + 7].Is(")")
            && tokens[start + 8].Is("=") && ReadDimension(tokens[start + 9]) == dimensions;
    }

    private static bool SameIdentifier(Token token, string name)
        => token.Identifier && DuckDbStructuredTypes.FoldName(token.Value) == DuckDbStructuredTypes.FoldName(name);

    private static int? ReadDimension(Token token)
        => !token.Quoted && !token.Literal
            && int.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is > 0 and <= DuckDbStructuredTypes.MaxVectorDimensions ? value : null;

    public static StructuredColumnMetadata? StructuredType(string sql)
    {
        if (sql.Equals("JSON", StringComparison.OrdinalIgnoreCase))
            return new(new(new JsonType(), StructuredStorage.Json), MetadataCoverage.Available);

        var tokens = Tokenize(sql);
        if (tokens.Count == 0 || !(tokens[^1].Is("]") || tokens[0].Is("STRUCT") || tokens[0].Is("MAP")
            || tokens[0].Is("UNION") || tokens[0].Is("VARIANT")))
            return null;

        var cursor = 0;
        var shape = ReadType(tokens, ref cursor, 0);
        return shape is not (null or ScalarType) && cursor == tokens.Count
            ? new(new(shape, StructuredStorage.Native), MetadataCoverage.Available)
            : new(null, MetadataCoverage.Unrepresentable);
    }

    private static StructuredType? ReadType(List<Token> tokens, ref int cursor, int depth)
    {
        if (cursor == tokens.Count || depth >= DuckDbStructuredTypes.MaxDepth)
            return null;
        var token = tokens[cursor++];
        StructuredType? shape = null;
        if (token.Is("STRUCT") && Take(tokens, ref cursor, "("))
            shape = ReadStruct(tokens, ref cursor, depth);
        else if (token.Is("MAP") && Take(tokens, ref cursor, "("))
        {
            var key = ReadType(tokens, ref cursor, depth + 1);
            if (key is ScalarType && Take(tokens, ref cursor, ",")
                && ReadType(tokens, ref cursor, depth + 1) is { } value && Take(tokens, ref cursor, ")"))
                shape = new MapType(key, value, true);
        }
        else if (token.Is("JSON"))
            shape = new JsonType();
        else if (!token.Quoted && !token.Literal && StructuredScalars.TryGetValue(token.Value, out var scalar))
            shape = new ScalarType(scalar);

        while (shape is not null && Take(tokens, ref cursor, "["))
        {
            if (++depth >= DuckDbStructuredTypes.MaxDepth || !Take(tokens, ref cursor, "]"))
                return null;
            shape = new ArrayType(shape, true);
        }
        return shape;
    }

    private static StructType? ReadStruct(List<Token> tokens, ref int cursor, int depth)
    {
        var fields = new List<StructField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            if (cursor == tokens.Count || !tokens[cursor].Identifier)
                return null;
            var name = tokens[cursor++].Value;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || !names.Add(DuckDbStructuredTypes.FoldName(name))
                || ReadType(tokens, ref cursor, depth + 1) is not { } type)
                return null;
            fields.Add(new(name, type, true));
        } while (Take(tokens, ref cursor, ","));
        return Take(tokens, ref cursor, ")") ? new StructType(fields) : null;
    }

    private static bool Take(List<Token> tokens, ref int cursor, string value)
    {
        if (cursor == tokens.Count || !tokens[cursor].Is(value))
            return false;
        cursor++;
        return true;
    }

    public static string? GeneratedExpression(string sql, string column, out string? rejectedClause)
    {
        rejectedClause = null;
        var tokens = Tokenize(sql);
        var open = tokens.FindIndex(token => token.Is("("));
        if (open < 0) return null;
        var end = Closing(tokens, open);
        if (end < 0) return null;
        foreach (var (start, stop) in Items(tokens, open + 1, end))
        {
            if (!tokens[start].Identifier || tokens[start].Value != column) continue;
            return ColumnGeneration(sql, tokens, start, stop, out rejectedClause);
        }
        return null;
    }

    private static string? ColumnGeneration(string sql, List<Token> tokens, int start, int stop, out string? rejectedClause)
    {
        rejectedClause = null;
        var depth = 0;
        for (var i = start + 1; i < stop; i++)
        {
            if (tokens[i].Is("(")) depth++;
            else if (tokens[i].Is(")")) depth--;
            if (depth != 0 || !tokens[i].Is("GENERATED")) continue;
            var expression = GenerationClause(sql, tokens, i + 1, stop);
            if (expression is null) rejectedClause = sql[tokens[i].Start..tokens[stop - 1].End];
            return expression;
        }
        return null;
    }

    private static string? GenerationClause(string sql, List<Token> tokens, int cursor, int stop)
    {
        if (cursor < stop && tokens[cursor].Is("ALWAYS")) cursor++;
        if (cursor + 1 >= stop || !tokens[cursor].Is("AS") || !tokens[cursor + 1].Is("(")) return null;
        var close = Closing(tokens, cursor + 1);
        if (close < 0 || close >= stop) return null;
        return sql[tokens[cursor + 1].End..tokens[close].Start].Trim();
    }

    public static MetadataIndexColumn[]? IndexColumns(string sql)
    {
        var tokens = Tokenize(sql);
        var on = tokens.FindIndex(token => token.Is("ON"));
        if (on < 0) return null;
        var open = tokens.FindIndex(on + 1, token => token.Is("("));
        if (open < 0 || tokens.Take(open).Any(token => token.Is("USING"))) return null;
        var close = Closing(tokens, open);
        if (close < 0 || tokens.Skip(close + 1).Any(token => !token.Is(";"))) return null;
        var columns = new List<MetadataIndexColumn>();
        foreach (var (start, stop) in Items(tokens, open + 1, close))
        {
            if (!tokens[start].Identifier) return null;
            bool? descending = null;
            if (stop - start == 2)
            {
                if (tokens[start + 1].Is("DESC")) descending = true;
                else if (tokens[start + 1].Is("ASC")) descending = false;
                else return null;
            }
            else if (stop - start != 1) return null;
            columns.Add(new(tokens[start].Value, descending));
        }
        return columns.Count > 0 ? columns.ToArray() : null;
    }

    public static bool TrySequence(string expression, out MetadataName? sequence)
    {
        sequence = null;
        var tokens = Tokenize(expression);
        while (tokens.Count > 1 && tokens[0].Is("(") && Closing(tokens, 0) == tokens.Count - 1)
            tokens = tokens.GetRange(1, tokens.Count - 2);
        if (tokens.Count != 4 || !tokens[0].Is("nextval") || !tokens[1].Is("(") || !tokens[2].Literal || !tokens[3].Is(")")) return false;
        try { sequence = MetadataNames.Parse(tokens[2].Value); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool UsesSequence(string expression) => Tokenize(expression).Any(token => token.Is("nextval"));

    private static IEnumerable<(int Start, int Stop)> Items(List<Token> tokens, int start, int end)
    {
        var depth = 0;
        var cursor = start;
        for (var i = start; i < end; i++)
        {
            if (tokens[i].Is("(")) depth++;
            else if (tokens[i].Is(")")) depth--;
            else if (tokens[i].Is(",") && depth == 0)
            {
                if (i > cursor) yield return (cursor, i);
                cursor = i + 1;
            }
        }
        if (end > cursor) yield return (cursor, end);
    }

    private static int Closing(List<Token> tokens, int start)
    {
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            if (tokens[i].Is("(")) depth++;
            else if (tokens[i].Is(")") && --depth == 0) return i;
        }
        return -1;
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            var start = i;
            if (sql[i] is '"' or '\'')
            {
                var token = ReadQuotedToken(sql, ref i);
                if (token is null) return [];
                tokens.Add(token);
            }
            else if (IsWordCharacter(sql[i])) tokens.Add(ReadWord(sql, ref i));
            else { i++; tokens.Add(new(sql[start..i], start, i)); }
        }
        return tokens;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';

    private static Token ReadWord(string sql, ref int position)
    {
        var start = position;
        while (position < sql.Length && IsWordCharacter(sql[position])) position++;
        return new(sql[start..position], start, position);
    }

    private static Token? ReadQuotedToken(string sql, ref int i)
    {
        var start = i;
        var quote = sql[i++];
        var value = new StringBuilder();
        var closed = false;
        while (i < sql.Length)
        {
            if (sql[i] != quote) { value.Append(sql[i++]); continue; }
            i++;
            if (i < sql.Length && sql[i] == quote) { value.Append(quote); i++; continue; }
            closed = true; break;
        }
        if (!closed) return null;
        return new(value.ToString(), start, i, Quoted: quote == '"', Literal: quote == '\'');
    }
}
