using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;

namespace DubUrl.Provider.Spark.Sql;

internal static class DatabricksForeignKeys
{
    internal static IReadOnlyList<SchemaSqlCommand> Plan(DatabricksSqlDialect dialect, ForeignKeyConstraint key,
        TableDefinitionContext? createdTarget)
    {
        var columns = key.TargetColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (columns.Count != key.TargetColumns.Count || key.SourceColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != key.SourceColumns.Count)
            throw new NotSupportedException($"Foreign key '{key.Name}' repeats a column under Unity Catalog naming rules.");

        if (createdTarget is null)
            return [ExternalTargetCheck(dialect, key)];
        if (createdTarget.PrimaryKey is { } primary && columns.SetEquals(primary.Columns))
            return [];
        if (createdTarget.UniqueKeys?.Any(unique => columns.SetEquals(unique.Columns)) == true)
        {
            if (dialect.SupportsUniqueKeys)
                return [];
            throw new NotSupportedException($"Foreign key '{key.Name}' references a UNIQUE key. Select WithInformationalKeys(supportsUnique: true) on compatible Databricks compute.");
        }
        throw new NotSupportedException($"Foreign key '{key.Name}' must reference all members of a PRIMARY KEY or supported UNIQUE key on '{key.TargetTable}'.");
    }

    private static SchemaSqlCommand ExternalTargetCheck(DatabricksSqlDialect dialect, ForeignKeyConstraint key)
    {
        var target = key.TargetTable;
        var info = target.Catalog is null ? "system.information_schema"
            : dialect.SchemaSqlRenderer.RenderIdentifier(dialect, target.Catalog) + ".information_schema";
        var catalog = target.Catalog is null ? "current_catalog()" : LowercaseLiteral(target.Catalog);
        var schema = target.Schema is null ? "current_schema()" : LowercaseLiteral(target.Schema);
        var kinds = dialect.SupportsUniqueKeys ? "'PRIMARY KEY', 'UNIQUE'" : "'PRIMARY KEY'";
        var members = string.Join(", ", key.TargetColumns.Select(LowercaseLiteral));
        var memberCount = FormattableString.Invariant($"{key.TargetColumns.Count}");
        var message = $"Foreign key '{key.Name}' requires a visible PRIMARY KEY"
            + (dialect.SupportsUniqueKeys ? " or UNIQUE key" : "")
            + $" on '{target}' matching ({string.Join(", ", key.TargetColumns)}).";
        if (!dialect.SupportsUniqueKeys)
            message += " For UNIQUE targets, select WithInformationalKeys(supportsUnique: true) on compatible Databricks compute.";

        var sql = $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM {info}.table_constraints AS t
                JOIN {info}.constraint_column_usage AS k
                  ON k.constraint_catalog = t.constraint_catalog
                 AND k.constraint_schema = t.constraint_schema
                 AND k.constraint_name = t.constraint_name
                 AND k.table_catalog = t.table_catalog
                 AND k.table_schema = t.table_schema
                 AND k.table_name = t.table_name
                WHERE t.table_catalog = {catalog}
                  AND t.table_schema = {schema}
                  AND t.table_name = {LowercaseLiteral(target.Name)}
                  AND t.constraint_type IN ({kinds})
                GROUP BY t.constraint_catalog, t.constraint_schema, t.constraint_name
                HAVING count(*) = {memberCount}
                   AND count(DISTINCT CASE WHEN lower(k.column_name) IN ({members}) THEN lower(k.column_name) END) = {memberCount}
            ) THEN 1 ELSE raise_error({SparkStringFormatter.Quote(message)}) END AS parent_key_valid;
            """;
        return new(sql, SchemaSqlOperation.ForeignKey, target.ToString());
    }

    private static string LowercaseLiteral(string value) => SparkStringFormatter.Quote(value.ToLowerInvariant());
}
