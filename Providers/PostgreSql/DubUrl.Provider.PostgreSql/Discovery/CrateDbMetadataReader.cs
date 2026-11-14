using System.Data.Common;
using System.Text.Json;
using DubUrl.Discovery;
using DubUrl.Providers.Discovery;
using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Discovery;

/// <summary>Reads CrateDB's object field paths, array types, and vector dimensions.</summary>
public sealed class CrateDbMetadataReader : StructuredCatalogMetadataReader
{
    public override Task<SchemaMetadata> ReadAsync(DbConnection connection, DiscoveryOptions options, CancellationToken cancellationToken = default)
        => ProviderSchemaDiagnostics.ReadAsync(() => base.ReadAsync(connection, options, cancellationToken));

    protected override string TablesSql => """
        SELECT table_schema AS schema_name,table_name
        FROM information_schema.tables
        WHERE table_type='BASE TABLE' AND NOT closed AND table_schema NOT IN ('pg_catalog','information_schema','sys','blob')
        """;

    protected override string ColumnsSql => """
        SELECT table_schema AS schema_name,table_name,column_name,ordinal_position AS ordinal,is_nullable AS nullable,
          data_type AS type_name,character_maximum_length AS length,numeric_precision AS "precision",numeric_scale AS scale,
          CAST(column_details AS TEXT) AS details
        FROM information_schema.columns
        WHERE table_schema NOT IN ('pg_catalog','information_schema','sys','blob')
        """;

    protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columnRows, List<CatalogRow> keyRows,
        List<CatalogRow> indexRows, List<CatalogRow> checkRows, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
    {
        var identity = Identity(row);
        var parsed = new List<Entry>();
        foreach (var column in columnRows.Where(column => Identity(column) == identity))
        {
            if (Entry.Read(column) is { } entry)
                parsed.Add(entry);
            else
                ProviderSchemaDiagnostics.Report(diagnose, identity, SchemaFeature.Columns,
                    "CrateDB column path metadata could not be read.", member: column.Text("column_name"),
                    nativeDefinition: column.Text("type_name"));
        }
        var entries = parsed.ToLookup(entry => entry.Root, StringComparer.Ordinal);
        var roots = entries.SelectMany(group => group.Where(entry => entry.Path.Length == 0))
            .ToDictionary(entry => entry.Root, StringComparer.Ordinal);
        var table = base.MapTable(row, roots.Values.Select(entry => entry.Row).ToList(), keyRows, indexRows, checkRows, options, diagnose);
        foreach (var column in table.Columns)
        {
            var root = roots[column.Name];
            if (!IsStructured(root.Row))
                continue;
            var shape = Decode(root, entries[root.Root], 0);
            Track(column, shape is null ? new(null, MetadataCoverage.Unrepresentable)
                : new(new(shape, StructuredStorage.Native), MetadataCoverage.Available));
        }
        return table;
    }

    protected override MetadataColumn MapColumn(CatalogRow row)
    {
        var column = base.MapColumn(row);
        return IsStructured(row) ? StructuredColumn(column) : column;
    }

    private static bool IsStructured(CatalogRow row)
        => row.Text("type_name") is { } type && (type is "object" or "float_vector" || type.EndsWith("_array", StringComparison.Ordinal));

    private static StructuredType? Decode(Entry entry, IEnumerable<Entry> entries, int depth)
    {
        if (depth >= CrateDbStructuredTypes.MaxDepth || entry.Row.Text("type_name") is not { } type)
            return null;
        if (type == "float_vector")
            return depth == 0 && entry.Row.Int("length") is > 0 and <= CrateDbStructuredTypes.MaxVectorDimensions and { } dimensions
                ? new VectorType(System.Data.DbType.Single, dimensions) : null;

        var ranks = 0;
        while (type.EndsWith("_array", StringComparison.Ordinal))
        {
            if (++ranks + depth >= CrateDbStructuredTypes.MaxDepth)
                return null;
            type = type[..^6];
        }
        // The catalog reports the same text_array for TEXT and VARCHAR(n) elements.
        if (ranks > 0 && type == "text")
            return null;
        StructuredType? shape = type == "object" ? ReadObject(entry, entries, depth + ranks)
            : entry.Row.Int("length") is null ? CrateDbStructuredTypes.ReadScalar(type) : null;
        if (shape is null)
            return null;
        for (var i = 0; i < ranks; i++)
            shape = new ArrayType(shape, true);
        return shape;
    }

    private static StructType? ReadObject(Entry entry, IEnumerable<Entry> entries, int depth)
    {
        if (entry.Policy != "strict")
            return null;
        var fields = new List<StructField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in entries.Where(item => item.Path.Length == entry.Path.Length + 1
            && item.Path.AsSpan(0, entry.Path.Length).SequenceEqual(entry.Path))
            .OrderBy(item => item.Row.Int("ordinal")))
        {
            var name = child.Path[^1];
            if (!CrateDbStructuredTypes.IsFieldName(name) || !names.Add(name) || child.Row.Bool("nullable") != true
                || Decode(child, entries, depth + 1) is not { } type)
                return null;
            fields.Add(new(name, type, true));
        }
        return fields.Count > 0 ? new(fields) : null;
    }

    private sealed record Entry(CatalogRow Row, string Root, string[] Path, string? Policy)
    {
        internal static Entry? Read(CatalogRow row)
        {
            if (row.Text("details") is not { } details || row.Text("column_name") is not { } column)
                return null;
            try
            {
                using var parsed = JsonDocument.Parse(details);
                var value = parsed.RootElement;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String || name.GetString() is not { } root
                    || !value.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array
                    || path.EnumerateArray().Any(segment => segment.ValueKind != JsonValueKind.String))
                    return null;
                var segments = path.EnumerateArray().Select(segment => segment.GetString()!).ToArray();
                if (segments.Length == 0 && root != column)
                    return null;
                var policy = value.TryGetProperty("policy", out var property) && property.ValueKind == JsonValueKind.String
                    ? property.GetString() : null;
                return new(row, root, segments, policy);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
