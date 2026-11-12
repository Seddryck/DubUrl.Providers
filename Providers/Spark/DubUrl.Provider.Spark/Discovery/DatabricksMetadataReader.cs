using System.Data.Common;
using System.Text.Json;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;

namespace DubUrl.Provider.Spark.Discovery;

public sealed class DatabricksMetadataReader : SparkCatalogMetadataReader
{
    private static readonly IReadOnlySet<SchemaFeature> Unsupported = new HashSet<SchemaFeature> { SchemaFeature.Indexes };
    private static readonly SchemaFeature[] KeyFeatures = [SchemaFeature.PrimaryKeys, SchemaFeature.ForeignKeys, SchemaFeature.UniqueConstraints];
    private CatalogRead? read;

    public override Task<SchemaMetadata> ReadAsync(DbConnection connection, DiscoveryOptions options, CancellationToken cancellationToken = default)
    {
        var snapshot = (DatabricksMetadataReader)MemberwiseClone();
        snapshot.read = new();
        return snapshot.ReadWithDiagnosticsAsync(connection, options, cancellationToken);
    }

    private protected override StructuredColumnMetadata? ReadStructuredType(CatalogRow row) => SparkStructuredCatalogType.ReadDelta(row);

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
        if (sql != ColumnsSql) return rows;
        var needsDefinition = NeedsDefinition(options);
        var result = new List<CatalogRow>();
        foreach (var group in rows.GroupBy(Identity))
        {
            var target = QualifiedName(group.Key);
            var structuredColumns = group.Where(row => SparkStructuredCatalogType.IsStructured(row.Text("type_name")))
                .Select(row => row.Text("column_name")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? json = null;
            if (structuredColumns.Count > 0)
            {
                try { json = await ScalarAsync(connection, $"DESCRIBE TABLE EXTENDED {target} AS JSON", cancellationToken).ConfigureAwait(false); }
                catch (DbException) { } // Older compute and restricted readers can still expose the type text.
            }
            var description = SparkStructuredCatalogType.DeltaDescription(json);
            Dictionary<string, string>? definitions = null;
            var definitionRequired = needsDefinition;
            if (needsDefinition)
            {
                try
                {
                    var ddl = await ScalarAsync(connection, $"SHOW CREATE TABLE {target}", cancellationToken).ConfigureAwait(false);
                    if (ddl is not null) definitions = SparkCatalogDefinition.Columns(ddl);
                }
                catch (DbException) when (read is not null)
                {
                    definitionRequired = false;
                    foreach (var feature in new[] { SchemaFeature.Defaults, SchemaFeature.Generation }.Where(options.Features.Contains))
                        read.Inaccessible(feature, group.Key, "The table definition could not be read with SHOW CREATE TABLE.");
                }
            }
            foreach (var row in group)
            {
                var name = row.Text("column_name")!;
                var column = row.With(("column_definition", definitions?.GetValueOrDefault(name)), ("definition_required", definitionRequired));
                if (structuredColumns.Contains(name) && description is { } metadata && metadata.Columns.TryGetValue(name, out var descriptor))
                {
                    var type = SparkStructuredCatalogType.Property(descriptor, "type");
                    var vectorCheck = SparkStructuredCatalogType.Text(type, "name") == "array"
                        && SparkStructuredCatalogType.Flag(type, "element_nullable") == true
                        && SparkStructuredCatalogType.Text(SparkStructuredCatalogType.Property(type, "element_type"), "name") == "float"
                        ? SparkStructuredCatalogType.Text(metadata.TableProperties, "delta.constraints." + DeltaVectorSql.ConstraintName(name)) : null;
                    column = column.With(("structured_json", type.ValueKind == JsonValueKind.Object ? type.GetRawText() : null),
                        ("nullable", SparkStructuredCatalogType.Flag(descriptor, "nullable") ?? row.Bool("nullable")),
                        ("vector_dimensions", DeltaVectorSql.ReadDimensions(name, vectorCheck)));
                }
                result.Add(column);
            }
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
        var references = keys.Where(row => row.Text("kind") == "FOREIGN KEY")
            .GroupBy(row => (Source: Identity(row), Key: row.Text("key_name")))
            .Where(group => group.All(HasVisibleTarget))
            .ToLookup(group => group.Key.Source, group => group.First());
        var queue = new Queue<MetadataName>(selected);
        while (queue.TryDequeue(out var source))
        {
            foreach (var row in references[source])
            {
                var target = new MetadataName(row.Text("target_table")!, row.Text("target_schema"), Catalog: row.Text("target_catalog"));
                if (available.Contains(target) && selected.Add(target)) queue.Enqueue(target);
            }
        }
    }

    private static bool HasVisibleTarget(CatalogRow row)
        => row.Text("target_table") is not null && row.Text("target_column") is not null;

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
        var invisible = keyRows.Where(key => Identity(key) == name && key.Text("kind") == "FOREIGN KEY" && !HasVisibleTarget(key))
            .Select(key => key.Text("key_name")).ToHashSet(StringComparer.Ordinal);
        if (invisible.Count > 0 && options.Features.Contains(SchemaFeature.ForeignKeys))
        {
            const string message = "An informational foreign key has a target that is not visible in catalog metadata.";
            if (read is null) diagnose(SchemaFeature.ForeignKeys, message);
            else read.Inaccessible(SchemaFeature.ForeignKeys, name, message);
            keyRows = keyRows.Where(key => Identity(key) != name || !invisible.Contains(key.Text("key_name"))).ToList();
        }

        if (checkRows.Count > 0) checkRows = PortableChecks(name, columnRows, checkRows, diagnose);

        var table = base.MapTable(row, columnRows, keyRows, indexRows, checkRows, options, diagnose);
        if (table.PrimaryKey is not null) DiagnoseKey(SchemaFeature.PrimaryKeys);
        if (table.ForeignKeys.Count > 0) DiagnoseKey(SchemaFeature.ForeignKeys);
        if (table.UniqueConstraints.Count > 0) DiagnoseKey(SchemaFeature.UniqueConstraints);
        return table;

        void DiagnoseKey(SchemaFeature feature) =>
            diagnose(feature, "Databricks keys are informational. Schema document v1 cannot preserve their lack of enforcement.");
    }

    private static List<CatalogRow> PortableChecks(MetadataName table, List<CatalogRow> columnRows, List<CatalogRow> checkRows,
        Action<SchemaFeature, string> diagnose)
    {
        var checks = new List<CatalogRow>();
        var requiredColumns = columnRows.Where(row => Identity(row) == table && row.Bool("nullable") == false)
            .Select(row => row.Text("column_name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vectors = columnRows.Where(row => Identity(row) == table && row.Int("vector_dimensions") is not null)
            .Select(row => row.Text("column_name")!).ToDictionary(DeltaVectorSql.ConstraintName, StringComparer.OrdinalIgnoreCase);
        foreach (var check in checkRows.Where(row => Identity(row) == table))
        {
            var sql = check.Text("expression")?.Trim() ?? "";
            if (check.Text("check_name") is { } name && vectors.TryGetValue(name, out var column)
                && DeltaVectorSql.ReadDimensions(column, sql) is not null)
                continue;
            const string prefix = "coalesce((";
            const string suffix = "), true)";
            if (sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && sql.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                checks.Add(check.With(("expression", sql[prefix.Length..^suffix.Length])));
            else if (DiscoveryExpressions.TryCheck(sql, check.Text("check_name"), out var comparison)
                && (!IsNonNull(comparison!.Left) || !IsNonNull(comparison.Right)))
            {
                diagnose(SchemaFeature.Checks, "A Delta comparison check can reject NULL, which the portable comparison does not preserve.");
            }
            else checks.Add(check);
        }
        return checks;

        bool IsNonNull(MetadataExpression expression) => expression.Kind switch
        {
            "column" => requiredColumns.Contains(expression.Name),
            "value" => expression.Value is not null and not DBNull,
            _ => false,
        };
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
