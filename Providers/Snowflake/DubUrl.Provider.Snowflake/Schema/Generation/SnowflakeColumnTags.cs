namespace DubUrl.Provider.Snowflake.Schema.Generation;

internal static class SnowflakeColumnTags
{
    internal static string Assignments(IReadOnlyDictionary<string, string> values)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var name = SnowflakeTableTags.RenderName(pair.Key);
            if (!names.Add(name) || pair.Value.EnumerateRunes().Count() > 256)
                throw new NotSupportedException($"Snowflake column tag '{pair.Key}' is duplicated or has a value longer than 256 characters.");
            return $"{name} = {SnowflakeTableTags.Literal(pair.Value)}";
        }));
    }

}
