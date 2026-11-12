using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Sql;

namespace DubUrl.Provider.Spark.Discovery;

public sealed class DatabricksMetadataReader : SparkCatalogMetadataReader
{
    private static readonly IReadOnlySet<SchemaFeature> Unsupported = new HashSet<SchemaFeature> { SchemaFeature.Indexes };
    private static readonly SchemaFeature[] KeyFeatures = [SchemaFeature.PrimaryKeys, SchemaFeature.ForeignKeys, SchemaFeature.UniqueConstraints];
    private readonly CatalogRead? read;

    public DatabricksMetadataReader() { }

    private DatabricksMetadataReader(CatalogRead read) => this.read = read;

    public override Task<SchemaMetadata> ReadAsync(DbConnection connection, DiscoveryOptions options, CancellationToken cancellationToken = default)
        => new DatabricksMetadataReader(new CatalogRead()).ReadWithDiagnosticsAsync(connection, options, cancellationToken);

    private async Task<SchemaMetadata> ReadWithDiagnosticsAsync(DbConnection connection, DiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var metadata = await base.ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        if (read!.Diagnostics.Count == 0) return metadata;
        var coverage = metadata.Coverage.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var diagnostic in read.Diagnostics)
            coverage[diagnostic.Feature] = diagnostic.Coverage;
        return metadata with { Coverage = coverage, Diagnostics = [.. metadata.Diagnostics, .. read.Diagnostics] };
    }

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
        if (sql == ViewsSql)
        {
            if (options.Tables.Count > 0) return [];
            var views = await ReadRows(connection, sql, options, useSystemCatalog: false, cancellationToken).ConfigureAwait(false);
            return views.Where(row => MatchesScope(Identity(row), options)).ToList();
        }

        var scope = read?.Scope ?? await ReadScopeAsync(connection, options, cancellationToken).ConfigureAwait(false);
        if (read is not null) read.Scope = scope;
        if (sql == TablesSql) return scope.Tables;
        if (sql == KeysSql && scope.Keys is not null) return scope.Keys;
        if (scope.Tables.Count == 0) return [];

        if (sql == ChecksSql)
        {
            var checks = new List<CatalogRow>();
            foreach (var table in scope.Tables)
            {
                var name = Identity(table);
                List<CatalogRow> properties;
                try
                {
                    properties = await QueryAsync(connection, $"SHOW TBLPROPERTIES {QualifiedName(name)}", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (DbException) when (read is not null)
                {
                    read.Inaccessible(SchemaFeature.Checks, name, "Delta check properties could not be read.");
                    continue;
                }
                const string prefix = "delta.constraints.";
                checks.AddRange(properties.Where(property => property.Text("key")?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(property => table.With(("check_name", property.Text("key")![prefix.Length..]), ("expression", property.Text("value")))));
            }
            return checks;
        }

        var rows = (await ReadRows(connection, sql, options, scope.Keys is not null, cancellationToken).ConfigureAwait(false))
            .Where(row => scope.Names.Contains(Identity(row))).ToList();
        if (sql != ColumnsSql || !NeedsDefinition(options)) return rows;
        var result = new List<CatalogRow>();
        foreach (var group in rows.GroupBy(Identity))
        {
            Dictionary<string, string>? definitions = null;
            var definitionRequired = true;
            try
            {
                var ddl = await ScalarAsync(connection, $"SHOW CREATE TABLE {QualifiedName(group.Key)}", cancellationToken).ConfigureAwait(false);
                if (ddl is not null) definitions = SparkCatalogDefinition.Columns(ddl);
            }
            catch (DbException) when (read is not null)
            {
                definitionRequired = false;
                foreach (var feature in new[] { SchemaFeature.Defaults, SchemaFeature.Generation }.Where(options.Features.Contains))
                    read.Inaccessible(feature, group.Key, "The table definition could not be read with SHOW CREATE TABLE.");
            }
            result.AddRange(group.Select(row => row.With(("column_definition", definitions?.GetValueOrDefault(row.Text("column_name")!)),
                ("definition_required", definitionRequired))));
        }
        return result;
    }

    private async Task<TableScope> ReadScopeAsync(DbConnection connection, DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var includeReferences = options.IncludeReferencedTables && options.Features.Contains(SchemaFeature.ForeignKeys);
        var rows = await ReadRows(connection, TablesSql, options, includeReferences, cancellationToken).ConfigureAwait(false);
        var selected = rows.Select(Identity).Where(name => MatchesScope(name, options)).ToHashSet();
        List<CatalogRow>? keys = null;
        if (includeReferences && selected.Count > 0)
        {
            try
            {
                keys = await ReadRows(connection, KeysSql, options, useSystemCatalog: true, cancellationToken).ConfigureAwait(false);
                IncludeReferences(selected, rows, keys);
                keys = keys.Where(row => selected.Contains(Identity(row))).ToList();
            }
            catch (DbException) when (read is not null)
            {
                keys = [];
                foreach (var feature in KeyFeatures.Where(options.Features.Contains))
                    read.Inaccessible(feature, null, "The catalog keys could not be read.");
            }
        }
        return new(rows.Where(row => selected.Contains(Identity(row))).ToList(), selected, keys);
    }

    private static void IncludeReferences(HashSet<MetadataName> selected, List<CatalogRow> tables, List<CatalogRow> keys)
    {
        var available = tables.Select(Identity).ToHashSet();
        var references = keys.Where(row => row.Text("kind") == "FOREIGN KEY").ToLookup(Identity);
        var queue = new Queue<MetadataName>(selected);
        while (queue.TryDequeue(out var source))
        {
            foreach (var row in references[source])
            {
                if (row.Text("target_table") is not { } name) continue;
                var target = new MetadataName(name, row.Text("target_schema"), Catalog: row.Text("target_catalog"));
                if (available.Contains(target) && selected.Add(target)) queue.Enqueue(target);
            }
        }
    }

    private static bool MatchesScope(MetadataName name, DiscoveryOptions options)
        => options.Databases.Count == 0
            && (options.Catalogs.Count == 0 || options.Catalogs.Contains(name.Catalog, StringComparer.Ordinal))
            && (options.Schemas.Count == 0 || options.Schemas.Contains(name.Schema, StringComparer.Ordinal))
            && (options.Tables.Count == 0 || options.Tables.Any(table => table.Name == name.Name && table.Database is null
                && (table.Schema is null || table.Schema == name.Schema) && (table.Catalog is null || table.Catalog == name.Catalog)));

    private async Task<List<CatalogRow>> ReadRows(DbConnection connection, string sql, DiscoveryOptions options,
        bool useSystemCatalog, CancellationToken cancellationToken)
    {
        if (useSystemCatalog)
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
            const string message = "An informational foreign key has a target that is not visible in catalog metadata.";
            if (read is null) diagnose(SchemaFeature.ForeignKeys, message);
            else read.Inaccessible(SchemaFeature.ForeignKeys, name, message);
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

    private sealed record TableScope(List<CatalogRow> Tables, HashSet<MetadataName> Names, List<CatalogRow>? Keys);

    // Provider registrations can reuse a reader across concurrent inspections.
    private sealed class CatalogRead
    {
        public TableScope? Scope { get; set; }
        public List<DiscoveryDiagnostic> Diagnostics { get; } = [];

        public void Inaccessible(SchemaFeature feature, MetadataName? name, string message)
            => Diagnostics.Add(new("SDS002", feature, MetadataCoverage.Inaccessible, message, name));
    }
}
