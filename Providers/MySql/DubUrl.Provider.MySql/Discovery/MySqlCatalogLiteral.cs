using System.Text;

namespace DubUrl.Provider.MySql.Discovery;

/// <summary>Decodes the UTF-8 literals that MySQL serializes in generated column defaults.</summary>
internal static class MySqlCatalogLiteral
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static string? Decode(string expression)
    {
        // MySQL prints the expression's bytes into COLUMN_DEFAULT using the binary character set.
        // That adds a second escape layer and exposes each original byte as a Latin-1 code point.
        var separator = expression.IndexOf('\\');
        if (separator < 0 || expression[..separator] is not ("_utf8mb4" or "_utf8mb3" or "_utf8"))
            return null;
        var sql = Unescape(expression.AsSpan(separator), catalog: true);
        if (sql is not { Length: >= 2 } || sql[0] != '\'' || sql[^1] != '\'')
            return null;
        var literal = Unescape(sql.AsSpan(1, sql.Length - 2), catalog: false);
        if (literal is null || literal.Any(character => character > byte.MaxValue))
            return null;
        try { return StrictUtf8.GetString(Encoding.Latin1.GetBytes(literal)); }
        catch (DecoderFallbackException) { return null; }
    }

    private static string? Unescape(ReadOnlySpan<char> input, bool catalog)
    {
        var result = new StringBuilder(input.Length);
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (character == '\'')
                return null;
            if (character == '\\')
            {
                if (++index == input.Length)
                    return null;
                char? unescaped = input[index] switch
                {
                    '\\' or '\'' => input[index],
                    '0' when !catalog => '\0',
                    'n' when !catalog => '\n',
                    'r' when !catalog => '\r',
                    'Z' when !catalog => '\u001a',
                    _ => null
                };
                if (unescaped is null)
                    return null;
                character = unescaped.Value;
            }
            result.Append(character);
        }
        return result.ToString();
    }
}
