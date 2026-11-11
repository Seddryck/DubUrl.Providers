using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Sql;

namespace DubUrl.Provider.Spark.Discovery;

public sealed class DatabricksMetadataReader : SparkCatalogMetadataReader
{
    private static readonly IReadOnlySet<SchemaFeature> Unsupported = new HashSet<SchemaFeature> { SchemaFeature.Indexes };
    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures => Unsupported;
    protected override string TablesSql => """
        SELECT table_catalog AS catalog_name, table_schema AS schema_name, table_name, comment
        FROM {info}.tables WHERE table_schema <> 'information_schema'
          AND table_type IN ('MANAGED', 'EXTERNAL', 'MANAGED_SHALLOW_CLONE', 'EXTERNAL_SHALLOW_CLONE')
        """;
    protected override string ColumnsSql => """
        SELECT c.table_catalog AS catalog_name, c.table_schema AS schema_name, c.table_name, c.column_name,
          c.ordinal_position AS ordinal, c.full_data_type AS type_name, c.is_nullable AS nullable,
          c.column_default AS default_expression, c.comment
        FROM {info}.columns c JOIN {info}.tables t ON t.table_catalog=c.table_catalog
          AND t.table_schema=c.table_schema AND t.table_name=c.table_name
        WHERE c.table_schema <> 'information_schema'
          AND t.table_type IN ('MANAGED', 'EXTERNAL', 'MANAGED_SHALLOW_CLONE', 'EXTERNAL_SHALLOW_CLONE')
        """;
    protected override string KeysSql => """
        SELECT k.table_catalog AS catalog_name, k.table_schema AS schema_name, k.table_name,
          k.constraint_name AS key_name, t.constraint_type AS kind, k.ordinal_position AS ordinal, k.column_name,
          p.table_catalog AS target_catalog, p.table_schema AS target_schema,
          p.table_name AS target_table, p.column_name AS target_column
        FROM {info}.key_column_usage k
        JOIN {info}.table_constraints t ON t.constraint_catalog=k.constraint_catalog
          AND t.constraint_schema=k.constraint_schema AND t.constraint_name=k.constraint_name
        LEFT JOIN {info}.referential_constraints r ON r.constraint_catalog=k.constraint_catalog
          AND r.constraint_schema=k.constraint_schema AND r.constraint_name=k.constraint_name
        LEFT JOIN {info}.key_column_usage p ON p.constraint_catalog=r.unique_constraint_catalog
          AND p.constraint_schema=r.unique_constraint_schema AND p.constraint_name=r.unique_constraint_name
          AND p.ordinal_position=k.position_in_unique_constraint
        WHERE k.table_schema <> 'information_schema'
        """;
    protected override string ViewsSql => """
        SELECT v.table_catalog AS catalog_name, v.table_schema AS schema_name, v.table_name,
          v.view_definition AS definition, c.column_name, c.ordinal_position AS ordinal,
          c.full_data_type AS type_name, c.is_nullable AS nullable, c.comment
        FROM {info}.views v LEFT JOIN {info}.columns c ON c.table_catalog=v.table_catalog
          AND c.table_schema=v.table_schema AND c.table_name=v.table_name
        WHERE v.table_schema <> 'information_schema'
        """;
    protected override string ChecksSql => "delta check properties";

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        if (sql == ViewsSql && options.Tables.Count > 0) return [];
        if (sql == ChecksSql)
        {
            var tables = await ReadRows(connection, TablesSql, options, cancellationToken).ConfigureAwait(false);
            var checks = new List<CatalogRow>();
            foreach (var table in tables.Where(row => InScope(Identity(row), options)))
            {
                var properties = await QueryAsync(connection, $"SHOW TBLPROPERTIES {QualifiedName(Identity(table))}", cancellationToken)
                    .ConfigureAwait(false);
                const string prefix = "delta.constraints.";
                checks.AddRange(properties.Where(property => property.Text("key")?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(property => table.With(("check_name", property.Text("key")![prefix.Length..]), ("expression", property.Text("value")))));
            }
            return checks;
        }

        var rows = await ReadRows(connection, sql, options, cancellationToken).ConfigureAwait(false);
        if (sql != ColumnsSql || !NeedsDefinition(options)) return rows;
        var result = new List<CatalogRow>();
        foreach (var group in rows.GroupBy(Identity))
        {
            if (!InScope(group.Key, options))
            {
                result.AddRange(group);
                continue;
            }
            Dictionary<string, string>? definitions = null;
            try
            {
                var ddl = await ScalarAsync(connection, $"SHOW CREATE TABLE {QualifiedName(group.Key)}", cancellationToken).ConfigureAwait(false);
                if (ddl is not null) definitions = SparkCatalogDefinition.Columns(ddl);
            }
            catch (DbException) { } // Inventory can be visible independently of the canonical DDL.
            result.AddRange(group.Select(row => row.With(("column_definition", definitions?.GetValueOrDefault(row.Text("column_name")!)),
                ("definition_required", true))));
        }
        return result;
    }

    private static bool InScope(MetadataName name, DiscoveryOptions options)
        => options.IncludeReferencedTables ||
            ((options.Catalogs.Count == 0 || options.Catalogs.Contains(name.Catalog, StringComparer.Ordinal))
            && (options.Schemas.Count == 0 || options.Schemas.Contains(name.Schema, StringComparer.Ordinal))
            && (options.Tables.Count == 0 || options.Tables.Any(table => table.Name == name.Name
                && (table.Schema is null || table.Schema == name.Schema) && (table.Catalog is null || table.Catalog == name.Catalog))));
    private async Task<List<CatalogRow>> ReadRows(DbConnection connection, string sql, DiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.IncludeReferencedTables)
        {
            // This Unity Catalog view includes cross-catalog key targets visible to the current principal.
            try { return await QueryAsync(connection, sql.Replace("{info}", "system.information_schema"), cancellationToken).ConfigureAwait(false); }
            catch (DbException) { } // Some workspaces do not expose the system catalog.
        }
        var catalogs = options.Catalogs.Concat(options.Tables.Select(table => table.Catalog).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (catalogs.Count == 0)
        {
            var current = await ScalarAsync(connection, "SELECT current_catalog()", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(current)) catalogs.Add(current);
        }
        var rows = new List<CatalogRow>();
        foreach (var catalog in catalogs)
        {
            var query = sql.Replace("{info}", Quote(catalog) + ".information_schema");
            rows.AddRange(await QueryAsync(connection, query, cancellationToken).ConfigureAwait(false));
        }
        return rows;
    }

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columnRows, List<CatalogRow> keyRows,
        List<CatalogRow> indexRows, List<CatalogRow> checkRows, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var name = Identity(row);
        var invisible = keyRows.Where(key => Identity(key) == name && key.Text("kind") == "FOREIGN KEY"
            && (key.Text("target_table") is null || key.Text("target_column") is null))
            .Select(key => key.Text("key_name")).ToHashSet(StringComparer.Ordinal);
        if (invisible.Count > 0 && options.Features.Contains(SchemaFeature.ForeignKeys))
        {
            diagnose(SchemaFeature.ForeignKeys, "An informational foreign key has a target that is not visible in catalog metadata.");
            keyRows = keyRows.Where(key => Identity(key) != name || !invisible.Contains(key.Text("key_name"))).ToList();
        }

        // Only a check emitted by DubUrl has this null-preserving wrapper. The shared metadata model
        // can represent its comparison; other predicates are diagnosed by the base reader.
        checkRows = checkRows.Select(check =>
        {
            var sql = check.Text("expression")?.Trim();
            const string prefix = "coalesce((";
            const string suffix = "), true)";
            return sql is not null && sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && sql.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? check.With(("expression", sql[prefix.Length..^suffix.Length])) : check;
        }).ToList();

        var table = base.MapTable(row, columnRows, keyRows, indexRows, checkRows, options, diagnose);
        if (table.PrimaryKey is not null) DiagnoseKey(SchemaFeature.PrimaryKeys);
        if (table.ForeignKeys.Count > 0) DiagnoseKey(SchemaFeature.ForeignKeys);
        if (table.UniqueConstraints.Count > 0) DiagnoseKey(SchemaFeature.UniqueConstraints);
        return table;

        void DiagnoseKey(SchemaFeature feature) =>
            diagnose(feature, "Databricks keys are informational. Schema document v1 cannot preserve their lack of enforcement.");
    }
}
