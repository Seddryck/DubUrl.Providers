using System.Text.Json;

namespace DubUrl.Providers;

internal static class ColumnAnnotationProperty
{
    internal const string Name = "duburl.column.annotations.v1";

    internal static bool IsReserved(string key)
        => string.Equals(key, Name, StringComparison.OrdinalIgnoreCase);

    internal static string Encode(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> annotations)
        => JsonSerializer.Serialize(annotations);

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Decode(string value)
    {
        using var json = JsonDocument.Parse(value);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Column annotations must be an object.");

        var columns = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var column in json.RootElement.EnumerateObject())
        {
            if (column.Value.ValueKind != JsonValueKind.Object || !columns.TryAdd(column.Name, ReadAnnotations(column.Value)))
                throw new JsonException("Column annotation entries must be unique objects.");
        }
        return columns;
    }

    private static IReadOnlyDictionary<string, string> ReadAnnotations(JsonElement column)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in column.EnumerateObject())
            if (entry.Value.ValueKind != JsonValueKind.String || !annotations.TryAdd(entry.Name, entry.Value.GetString()!))
                throw new JsonException("Column annotation keys must be unique and have string values.");
        return annotations;
    }
}
