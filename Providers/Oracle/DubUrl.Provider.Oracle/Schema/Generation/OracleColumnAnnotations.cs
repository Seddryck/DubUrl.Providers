using DubUrl.Discovery;

namespace DubUrl.Provider.Oracle.Schema.Generation;

internal static class OracleColumnAnnotations
{
    internal const string CatalogQuery = """
        SELECT annotation_owner AS schema_name, object_name AS table_name, column_name,
          annotation_name, annotation_value
        FROM all_annotations_usage
        WHERE object_type = 'TABLE' AND column_name IS NOT NULL AND domain_name IS NULL
        """;

    internal static bool Supports(Version version)
        => version.Major >= 23 || (version.Major == 19 && version.Minor >= 28);

    internal static IReadOnlyDictionary<string, string> Read(IEnumerable<CatalogRow> rows, MetadataName table,
        string column, List<DiscoveryDiagnostic> diagnostics)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(row => row.Text("annotation_name"), StringComparer.Ordinal))
        {
            var values = group.Select(row => row.Text("annotation_value")).Distinct(StringComparer.Ordinal).ToArray();
            if (group.Key is { } key && values is [string value] && IsWritable(key, value))
                annotations.Add(key, value);
            else
                diagnostics.Add(new("SDI009", SchemaFeature.ColumnAnnotations, MetadataCoverage.Unrepresentable,
                    $"Oracle annotation '{group.Key}' on column '{column}' cannot be represented as writable text.", table));
        }
        return annotations;
    }

    internal static string Render(IReadOnlyDictionary<string, string> annotations)
        => string.Join(", ", annotations.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            if (!IsWritable(pair.Key, pair.Value))
                throw new NotSupportedException($"Oracle column annotation '{pair.Key}' requires a valid name and nonempty text.");
            return $"ADD \"{pair.Key}\" '{pair.Value.Replace("'", "''", StringComparison.Ordinal)}'";
        }));

    private static bool IsWritable(string key, string value)
        => key.Length <= 1024 && !string.IsNullOrWhiteSpace(key) && !key.Contains('"') && !key.Contains('\0')
            && value.Length is > 0 and <= 4000 && !value.Contains('\0');
}
