using System.Data;
using System.Data.Common;
using System.Text.Json;
using DubUrl.Discovery;
using DubUrl.Provider.Hive.Sql;
using DubUrl.Providers;

namespace DubUrl.Provider.Hive.Discovery;

public sealed class HiveMetadataReader : CatalogSchemaMetadataReader, ISchemaMetadataReader
{
    protected override bool SupportsColumnAnnotations => true;
    protected override bool SupportsAnnotations => true;
    protected override IReadOnlySet<SchemaFeature> UnsupportedFeatures { get; } = Enum.GetValues<SchemaFeature>()
        .Where(feature => feature is not (SchemaFeature.Tables or SchemaFeature.Columns or SchemaFeature.Comments
            or SchemaFeature.Annotations or SchemaFeature.ColumnAnnotations)).ToHashSet();

    protected override string TablesSql => "SHOW TABLES";
    protected override string ColumnsSql => "DESCRIBE";
    protected override string KeysSql => "";

    protected override async Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
        DiscoveryOptions options, CancellationToken cancellationToken)
    {
        if (sql != TablesSql && sql != ColumnsSql) return [];
        var tables = await ReadTablesAsync(connection, options, cancellationToken).ConfigureAwait(false);
        if (sql == TablesSql) return tables;
        var columns = new List<CatalogRow>();
        foreach (var table in tables)
        {
            var rows = await QueryAsync(connection, $"DESCRIBE {Target(Identity(table))}", cancellationToken).ConfigureAwait(false);
            var ordinal = 0;
            foreach (var row in rows.TakeWhile(row => !string.IsNullOrWhiteSpace(row.Text("col_name"))
                && !row.Text("col_name")!.StartsWith("# ", StringComparison.Ordinal)))
                columns.Add(table.With(("column_name", row.Text("col_name")), ("type_name", row.Text("data_type")),
                    ("comment", row.Text("comment")), ("nullable", true), ("ordinal", ++ordinal)));
        }
        return columns;
    }

    private static async Task<List<CatalogRow>> ReadTablesAsync(DbConnection connection, DiscoveryOptions options,
        CancellationToken cancellationToken)
    {
        var databases = options.Schemas.Concat(options.Tables.Select(table => table.Schema).OfType<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (databases.Length == 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_database()";
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string database)
                databases = [database];
        }

        var tables = new List<CatalogRow>();
        foreach (var database in databases)
        {
            var name = HiveSchemaSqlRenderer.QuoteIdentifier(database);
            var rows = await QueryAsync(connection, $"SHOW TABLES IN {name}", cancellationToken).ConfigureAwait(false);
            var views = await QueryAsync(connection, $"SHOW VIEWS IN {name}", cancellationToken).ConfigureAwait(false);
            var viewNames = views.Select(row => row.Text("tab_name") ?? row.Text("view_name")).ToHashSet(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.Text("tab_name") is not { } table || viewNames.Contains(table)
                    || (options.Tables.Count > 0 && !options.Tables.Any(filter =>
                        filter.Name == table && (filter.Schema is null || filter.Schema == database)))) continue;
                tables.Add(new CatalogRow(new Dictionary<string, object?> { ["schema_name"] = database, ["table_name"] = table }));
            }
        }
        return tables;
    }

    public new async Task<SchemaMetadata> ReadAsync(DbConnection connection, DiscoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        var metadata = await base.ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
        if (!options.Features.Any(feature => feature is SchemaFeature.Annotations or SchemaFeature.ColumnAnnotations or SchemaFeature.Comments))
            return metadata;

        var diagnostics = metadata.Diagnostics.ToList();
        var tables = new List<MetadataTable>(metadata.Tables.Count);
        foreach (var table in metadata.Tables)
            tables.Add(await ReadPropertiesAsync(connection, table, options, diagnostics, cancellationToken).ConfigureAwait(false));
        return metadata with { Tables = tables, Diagnostics = diagnostics };
    }

    private static async Task<MetadataTable> ReadPropertiesAsync(DbConnection connection, MetadataTable table,
        DiscoveryOptions options, List<DiscoveryDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var readComments = options.Features.Contains(SchemaFeature.Comments);
        var readAnnotations = options.Features.Contains(SchemaFeature.ColumnAnnotations);
        var readTableAnnotations = options.Features.Contains(SchemaFeature.Annotations);
        try
        {
            var rows = await QueryAsync(connection, $"SHOW TBLPROPERTIES {Target(table.Identity)}", cancellationToken).ConfigureAwait(false);
            if (readComments)
                table = table with { Comment = rows.FirstOrDefault(row => PropertyName(row) == "comment") is { } comment
                    ? PropertyValue(comment) : null };
            if (readTableAnnotations)
                table = table with { Annotations = rows.Where(row => PropertyName(row) is { } key
                    && !HiveSchemaSqlRenderer.IsManagedProperty(key) && PropertyValue(row) is not null)
                    .ToDictionary(row => PropertyName(row)!, row => PropertyValue(row)!, StringComparer.Ordinal) };
            if (!readAnnotations) return table;
            var properties = rows.Where(row => PropertyName(row) is { } key && ColumnAnnotationProperty.IsReserved(key)).ToArray();
            if (properties.Length == 0) return table;
            if (properties.Length != 1 || PropertyValue(properties[0]) is not { } value) throw new JsonException();
            var annotations = ColumnAnnotationProperty.Decode(value);
            if (annotations.Keys.Except(table.Columns.Select(column => column.Name), StringComparer.Ordinal).Any())
                diagnostics.Add(new("SDH002", SchemaFeature.ColumnAnnotations, MetadataCoverage.Unrepresentable,
                    "The stored annotations reference a missing column.", table.Identity));
            return table with { Columns = table.Columns.Select(column => annotations.TryGetValue(column.Name, out var items)
                ? column with { Annotations = items } : column).ToArray() };
        }
        catch (JsonException)
        {
            diagnostics.Add(new("SDH002", SchemaFeature.ColumnAnnotations, MetadataCoverage.Unrepresentable,
                "The stored column annotations are malformed or ambiguous.", table.Identity));
        }
        catch (DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readAnnotations)
                diagnostics.Add(new("SDH001", SchemaFeature.ColumnAnnotations, MetadataCoverage.Inaccessible,
                    "Hive column annotation properties could not be read.", table.Identity));
            if (readComments)
                diagnostics.Add(new("SDH001", SchemaFeature.Comments, MetadataCoverage.Inaccessible,
                    "Hive table comments could not be read.", table.Identity));
            if (readTableAnnotations)
                diagnostics.Add(new("SDH001", SchemaFeature.Annotations, MetadataCoverage.Inaccessible,
                    "Hive table annotation properties could not be read.", table.Identity));
        }
        return table;
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        return row.Text("type_name")?.ToLowerInvariant() switch
        {
            "string" => column with { Type = DbType.String },
            "tinyint" => column with { Type = DbType.SByte },
            "float" => column with { Type = DbType.Single },
            _ => column
        };
    }

    private static string? PropertyName(CatalogRow row) => row.Text("prpt_name") ?? row.Text("key");
    private static string? PropertyValue(CatalogRow row) => row.Text("prpt_value") ?? row.Text("value");

    private static string Target(MetadataName name)
        => $"{HiveSchemaSqlRenderer.QuoteIdentifier(name.Schema!)}.{HiveSchemaSqlRenderer.QuoteIdentifier(name.Name)}";
}
