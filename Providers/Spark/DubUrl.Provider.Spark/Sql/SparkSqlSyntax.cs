namespace DubUrl.Provider.Spark.Sql;

internal static class SparkSqlSyntax
{
    internal readonly record struct Token(string Text, int Start, int End, int Depth)
    {
        internal bool Is(string value) => Text.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    // Schema commands are supplied individually by the planner. Quoted delimiters must not affect clause boundaries.
    internal static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var depth = 0;
        for (var i = 0; i < sql.Length;)
        {
            var start = i;
            var character = sql[i++];
            if (char.IsWhiteSpace(character))
                continue;
            if (character is '\'' or '"' or '`')
            {
                while (i < sql.Length)
                {
                    var quoted = sql[i++];
                    if (character is '\'' or '"' && quoted == '\\' && i < sql.Length)
                        i++;
                    else if (quoted == character)
                    {
                        if (i < sql.Length && sql[i] == character) i++;
                        else break;
                    }
                }
            }
            else if (IsWord(character))
            {
                while (i < sql.Length && IsWord(sql[i])) i++;
            }
            if (character == ')') depth--;
            tokens.Add(new(sql[start..i], start, i, depth));
            if (character == '(') depth++;
        }
        return tokens;
    }

    private static bool IsWord(char value) => char.IsLetterOrDigit(value) || value == '_';
}
