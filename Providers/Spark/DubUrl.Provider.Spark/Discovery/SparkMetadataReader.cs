using System.Data.Common;
using System.Text.Json;
using DubUrl.Discovery;
using DubUrl.Providers;
using DubUrl.Schema;

namespace DubUrl.Provider.Spark.Discovery;

public sealed class SparkMetadataReader : SparkCatalogMetadataReader
{
    private Dictionary<MetadataName, IReadOnlyDictionary<string, string>?>? tableAnnotations;
    protected override bool SupportsAnnotations => true;
    protected override bool SupportsColumnAnnotations => true;
    private protected override StructuredColumnMetadata? ReadStructuredType(CatalogRow row) => SparkStructuredCatalogType.ReadSpark(row);

    public override Task<SchemaMetadata> ReadAsync(DbConnection connection, DiscoveryOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.Features.Contains(SchemaFeature.Annotations) && !options.Features.Contains(SchemaFeature.ColumnAnnotations))
            return base.ReadAsync(connection, options, cancellationToken);

        var snapshot = (SparkMetadataReader)MemberwiseClone();
        snapshot.tableAnnotations = [];
        return snapshot.ReadWithAnnotationsAsync(connection, options, cancellationToken);
    }

    private async Task<SchemaMetadata> ReadWithAnnotationsAsync(DbConnection connection, DiscoveryOptions options, CancellationToken cancellationToken)
    {
        var metadata = await base.ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var features = options.Features.Where(feature => feature is SchemaFeature.Annotations or SchemaFeature.ColumnAnnotations);
        var failures = tableAnnotations!.Where(pair => pair.Value is null)
            .SelectMany(pair => features.Select(feature => new DiscoveryDiagnostic("SDS002", feature,
                MetadataCoverage.Inaccessible, "Table properties could not be read.", pair.Key))).ToArray();
        if (failures.Length == 0)
            return metadata;
        var coverage = metadata.Coverage.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var failure in failures)
            coverage[failure.Feature] = failure.Coverage;
        return metadata with
        {
            Coverage = coverage,
            Diagnostics = [.. metadata.Diagnostics, .. failures],
        };
    }

    private static readonly IReadOnlySet<SchemaFeature> Unsupported = new HashSet<SchemaFeature>
    {
        SchemaFeature.PrimaryKeys, SchemaFeature.ForeignKeys, SchemaFeature.UniqueConstraints,
        SchemaFeature.Indexes, SchemaFeature.Checks, SchemaFeature.Generation,
    };
    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures => Unsupported;
    protected override string TablesSql => "SHOW TABLES";
    protected override string ColumnsSql => "DESCRIBE TABLE EXTENDED";
    protected override string KeysSql => "unsupported Spark keys";
    protected override string ViewsSql => "SHOW VIEWS";

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        if (sql == KeysSql) return [];
        var catalogs = options.Catalogs.Concat(options.Tables.Select(table => table.Catalog).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (catalogs.Count == 0)
        {
            var current = await ScalarAsync(connection, "SELECT current_catalog()", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(current)) catalogs.Add(current);
        }

        var results = new List<CatalogRow>();
        foreach (var catalog in catalogs)
        {
            var namespaces = options.Schemas.Concat(options.Tables.Select(table => table.Schema).OfType<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var schemas = await QueryAsync(connection, $"SHOW NAMESPACES IN {Quote(catalog)}", cancellationToken).ConfigureAwait(false);
            var available = schemas.Select(row => row.Text("namespace") ?? row.Text("databaseName")).OfType<string>();
            namespaces = (namespaces.Count == 0 ? available : available.Where(name => namespaces.Contains(name, StringComparer.Ordinal))).ToList();
            foreach (var schema in namespaces)
            {
                var scope = $"{Quote(catalog)}.{Quote(schema)}";
                var views = await QueryAsync(connection, $"SHOW VIEWS IN {scope}", cancellationToken).ConfigureAwait(false);
                var viewNames = views.Where(view => view.Bool("isTemporary") != true)
                    .Select(view => view.Text("viewName")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (sql == ViewsSql)
                {
                    foreach (var view in viewNames.Order(StringComparer.Ordinal))
                        await AddRelation(new(view, schema, Catalog: catalog), isView: true).ConfigureAwait(false);
                    continue;
                }
                var tables = await QueryAsync(connection, $"SHOW TABLES IN {scope}", cancellationToken).ConfigureAwait(false);
                foreach (var table in tables.Where(table => table.Bool("isTemporary") != true))
                {
                    var name = table.Text("tableName");
                    if (name is null || viewNames.Contains(name)
                        || (options.Tables.Count > 0 && !options.Tables.Any(filter => filter.Name == name
                            && (filter.Catalog is null || filter.Catalog == catalog) && (filter.Schema is null || filter.Schema == schema))))
                        continue;
                    await AddRelation(new(name, schema, Catalog: catalog), isView: false).ConfigureAwait(false);
                }
            }
        }
        return results;

        async Task AddRelation(MetadataName identity, bool isView)
        {
            var target = QualifiedName(identity);
            var relation = Row(("catalog_name", identity.Catalog), ("schema_name", identity.Schema), ("table_name", identity.Name));
            if (sql == TablesSql && !options.Features.Contains(SchemaFeature.Comments)
                && tableAnnotations is null)
            {
                results.Add(relation);
                return;
            }
            var description = await QueryAsync(connection, $"DESCRIBE TABLE EXTENDED {target}", cancellationToken).ConfigureAwait(false);
            if (sql == TablesSql)
            {
                var comment = description.LastOrDefault(row => row.Text("col_name") == "Comment")?.Text("data_type");
                relation = relation.With(("comment", comment));
                if (tableAnnotations is not null)
                {
                    var delta = description.Any(row => row.Text("col_name") == "Provider"
                        && string.Equals(row.Text("data_type"), "delta", StringComparison.OrdinalIgnoreCase));
                    try
                    {
                        var properties = await QueryAsync(connection, $"SHOW TBLPROPERTIES {target}", cancellationToken).ConfigureAwait(false);
                        tableAnnotations[identity] = SparkTableProperties.Read(properties,
                            key => !ColumnAnnotationProperty.IsReserved(key)
                                && (SparkTableProperties.IsReserved(key) || (delta && SparkTableProperties.IsDelta(key))));
                    }
                    catch (DbException)
                    {
                        tableAnnotations[identity] = null;
                    }
                }
                results.Add(relation);
                return;
            }

            var columns = description.TakeWhile(column => !string.IsNullOrWhiteSpace(column.Text("col_name"))
                && column.Text("col_name")?.StartsWith("# ", StringComparison.Ordinal) != true).ToArray();
            var parquet = description.LastOrDefault(row => row.Text("col_name") == "Provider")?.Text("data_type")
                ?.Equals("PARQUET", StringComparison.OrdinalIgnoreCase) == true;
            string? ddl = null;
            if (isView || NeedsDefinition(options) || columns.Any(column => SparkStructuredCatalogType.IsStructured(column.Text("data_type"))))
            {
                try { ddl = await ScalarAsync(connection, $"SHOW CREATE TABLE {target}", cancellationToken).ConfigureAwait(false); }
                catch (DbException) { }
            }
            var definitions = !isView && !string.IsNullOrWhiteSpace(ddl) ? SparkCatalogDefinition.Columns(ddl) : null;
            var ordinal = 0;
            foreach (var column in columns)
            {
                var name = column.Text("col_name")!;
                results.Add(relation.With(("column_name", name), ("ordinal", ++ordinal), ("nullable", true),
                    ("type_name", column.Text("data_type")), ("comment", column.Text("comment")),
                    ("column_definition", definitions?.GetValueOrDefault(name)), ("definition_required", !isView && NeedsDefinition(options)),
                    ("definition", isView ? ddl : null), ("parquet", parquet)));
            }
            if (isView && ordinal == 0) results.Add(relation.With(("definition", ddl)));
        }
    }

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columnRows, List<CatalogRow> keyRows,
        List<CatalogRow> indexRows, List<CatalogRow> checkRows, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var table = base.MapTable(row, columnRows, keyRows, indexRows, checkRows, options, diagnose);
        if (tableAnnotations?.GetValueOrDefault(table.Identity) is not { } properties)
            return table;

        if (options.Features.Contains(SchemaFeature.ColumnAnnotations))
            table = MapColumnAnnotations(table, properties, diagnose);
        return options.Features.Contains(SchemaFeature.Annotations)
            ? table with { Annotations = properties.Where(pair => !ColumnAnnotationProperty.IsReserved(pair.Key)).ToDictionary(StringComparer.Ordinal) }
            : table;
    }

    private MetadataTable MapColumnAnnotations(MetadataTable table, IReadOnlyDictionary<string, string> properties,
        Action<SchemaFeature, string> diagnose)
    {
        var stored = properties.Where(pair => ColumnAnnotationProperty.IsReserved(pair.Key)).ToArray();
        if (stored.Length == 0) return table;
        try
        {
            if (stored.Length != 1)
                throw new JsonException();
            var annotations = ColumnAnnotationProperty.Decode(stored[0].Value);
            if (annotations.Keys.Except(table.Columns.Select(column => column.Name), StringComparer.Ordinal).Any())
                diagnose(SchemaFeature.ColumnAnnotations, "The stored annotations reference a missing column.");
            return table with { Columns = table.Columns.Select(column => annotations.TryGetValue(column.Name, out var values)
                ? WithAnnotations(column, values) : column).ToArray() };
        }
        catch (JsonException)
        {
            diagnose(SchemaFeature.ColumnAnnotations, "The stored column annotations are malformed or ambiguous.");
            return table;
        }
    }
}
