using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Hive.Sql;

internal sealed class HiveSchemaSqlRenderer : StandardSchemaSqlRenderer, ITableAnnotationSqlRenderer
{
    internal static new HiveSchemaSqlRenderer Instance { get; } = new();

    private static readonly HashSet<string> ManagedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "comment", "external", "external.table.purge", "immutable", "transactional", "transactional_properties",
        "no_auto_compaction", "auto.purge", "last_modified_time", "last_modified_user", "transient_lastDdlTime",
        "numFiles", "numPartitions", "numRows", "rawDataSize", "totalSize", "column_stats_accurate",
        "hbase.table.name", "storage_handler", "bucketing_version",
    };

    public IReadOnlyList<SchemaSqlCommand> PlanTableAnnotations(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, string> annotations)
    {
        if (annotations.Count == 0)
            return [];

        var name = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        foreach (var key in annotations.Keys)
        {
            if (key.Length == 0 || ManagedKeys.Contains(key) || key.StartsWith("orc.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("compactor.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("compactorthreshold.", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Hive table property '{key}' cannot be set as an annotation on {name}.");
        }
        var properties = annotations.OrderBy(annotation => annotation.Key, StringComparer.Ordinal).Select(annotation =>
            $"{Quote(annotation.Key)} = {Quote(annotation.Value)}");
        return [new($"ALTER TABLE {name} SET TBLPROPERTIES ({string.Join(", ", properties)});", SchemaSqlOperation.Comment, table.ToString())];
    }

    private static string Quote(string value) => "'" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "'";
}
