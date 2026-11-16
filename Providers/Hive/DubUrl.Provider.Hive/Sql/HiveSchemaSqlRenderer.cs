using DubUrl.Querying.Dialects;
using DubUrl.Providers;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Hive.Sql;

internal sealed class HiveSchemaSqlRenderer : StandardSchemaSqlRenderer, ITableAnnotationSqlRenderer, IColumnAnnotationSqlRenderer,
    IColumnDefinitionSqlRenderer
{
    internal static new HiveSchemaSqlRenderer Instance { get; } = new();

    public override bool SupportsCommentRendering => true;

    public ColumnDefinitionPlan? RenderColumnDefinition(IDialect dialect, ColumnDefinitionContext column)
        => column.Nullable ? new(column.Type, ColumnDefinitionClauses.Nullability) : null;

    public override string RenderColumnComment(IDialect dialect, string description) => " COMMENT " + Quote(description);
    public override string RenderIdentifier(IDialect dialect, string name) => QuoteIdentifier(name);
    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(catalog, "Catalog", dialect);
        RejectQualifier(database, "Database", dialect);
        return schema is null ? QuoteIdentifier(name) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";
    }
    public override string RenderComment(IDialect dialect, SchemaSqlObjectName table, string? column, string description)
        => column is null ? RenderProperty(RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name),
            "comment", description) : string.Empty;

    internal static string QuoteIdentifier(string value) => "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`";

    public IReadOnlyList<SchemaSqlCommand> PlanColumnAnnotations(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> annotations)
    {
        var name = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        return [new(RenderProperty(name, ColumnAnnotationProperty.Name, ColumnAnnotationProperty.Encode(annotations)),
            SchemaSqlOperation.Comment, table.ToString())];
    }

    private static string RenderProperty(string name, string key, string value)
        => $"ALTER TABLE {name} SET TBLPROPERTIES ({Quote(key)} = {Quote(value)});";

    private static readonly HashSet<string> ManagedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "comment", "external", "external.table.purge", "immutable", "transactional", "transactional_properties",
        "no_auto_compaction", "auto.purge", "last_modified_time", "last_modified_user", "transient_lastDdlTime",
        "numFiles", "numPartitions", "numRows", "rawDataSize", "totalSize", "column_stats_accurate",
        "hbase.table.name", "storage_handler", "bucketing_version",
    };

    internal static bool IsManagedProperty(string key)
        => key.Length == 0 || ColumnAnnotationProperty.IsReserved(key) || ManagedKeys.Contains(key)
            || key.StartsWith("orc.", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("compactor.", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("compactorthreshold.", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SchemaSqlCommand> PlanTableAnnotations(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, string> annotations)
    {
        if (annotations.Count == 0)
            return [];

        var name = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        foreach (var key in annotations.Keys)
        {
            if (IsManagedProperty(key))
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
