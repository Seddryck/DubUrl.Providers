using System.Text.RegularExpressions;

namespace DubUrl.Provider.Snowflake.Schema.Generation;

internal static class SnowflakeColumnTags
{
    private static readonly Regex Unquoted = new(@"\A[A-Z_][A-Z0-9_$]*\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private const string Part = """(?<part>[A-Za-z_][A-Za-z0-9_$]*|"(?:[^"\x00]|"")+")""";
    private static readonly Regex Qualified = new($@"\A{Part}\.{Part}\.{Part}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string Key(string database, string schema, string name)
        => string.Join('.', new[] { database, schema, name }.Select(part =>
            Unquoted.IsMatch(part) ? part : SnowflakeStructuredTypes.QuoteIdentifier(part)));

    internal static string Assignments(IReadOnlyDictionary<string, string> values)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var name = RenderName(pair.Key);
            if (!names.Add(name) || pair.Value.EnumerateRunes().Count() > 256)
                throw new NotSupportedException($"Snowflake column tag '{pair.Key}' is duplicated or has a value longer than 256 characters.");
            return $"{name} = {Literal(pair.Value)}";
        }));
    }

    private static string RenderName(string key)
    {
        var match = Qualified.Match(key);
        if (!match.Success)
            throw new NotSupportedException($"Snowflake column annotation '{key}' requires a qualified tag name.");
        var parts = match.Groups["part"].Captures.Select(capture => capture.Value[0] == '"'
            ? capture.Value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : capture.Value.ToUpperInvariant()).ToArray();
        if (parts[0] == "SNOWFLAKE" || parts.Any(part => part.EnumerateRunes().Count() > 255))
            throw new NotSupportedException($"Snowflake annotation '{key}' cannot be applied as a user-defined tag.");
        return string.Join('.', parts.Select(SnowflakeStructuredTypes.QuoteIdentifier));
    }

    private static string Literal(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "''", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal) + "'";
}
