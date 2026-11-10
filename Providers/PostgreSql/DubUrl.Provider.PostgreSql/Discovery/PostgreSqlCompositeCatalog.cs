using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Discovery;

internal sealed class PostgreSqlCompositeCatalog(IEnumerable<CatalogRow> attributes)
{
    private readonly ILookup<long?, CatalogRow> types = attributes.ToLookup(attribute => attribute.Long("composite_oid"));
    private readonly Dictionary<(long Oid, int Depth), StructType?> decoded = new();

    internal const string Sql = """
        SELECT t.oid AS composite_oid,a.attnum AS ordinal,a.attname AS field_name,a.atttypmod AS field_modifier,
          a.attnotnull AS field_not_null,a.attcollation=ft.typcollation AS field_default_collation,
          ft.oid AS field_type_oid,ft.typname AS field_type,fn.nspname AS field_type_schema,ft.typtype='c' AS field_composite
        FROM pg_catalog.pg_type t
        JOIN pg_catalog.pg_class c ON c.oid=t.typrelid AND c.relkind='c'
        JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
        JOIN pg_catalog.pg_type ft ON ft.oid=a.atttypid
        JOIN pg_catalog.pg_namespace fn ON fn.oid=ft.typnamespace
        WHERE t.typtype='c'
        """;

    internal StructuredColumnMetadata Decode(long? oid)
        => oid is { } id && Decode(id, 0) is { } shape
            ? new(new(shape, StructuredStorage.Native), MetadataCoverage.Available)
            : new(null, MetadataCoverage.Unrepresentable);

    private StructType? Decode(long oid, int depth)
    {
        if (depth >= PostgreSqlCompositeType.MaxDepth)
            return null;
        var key = (oid, depth);
        if (decoded.TryGetValue(key, out var cached))
            return cached;
        var shape = ReadFields(types[oid], depth);
        decoded.Add(key, shape);
        return shape;
    }

    private StructType? ReadFields(IEnumerable<CatalogRow> attributes, int depth)
    {
        var fields = new List<StructField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes.OrderBy(field => field.Int("ordinal")))
        {
            if (attribute.Text("field_name") is not { } name || !PostgreSqlCompositeType.ValidIdentifier(name)
                || !names.Add(name) || attribute.Int("field_modifier") != -1 || attribute.Bool("field_not_null") != false
                || attribute.Bool("field_default_collation") != true || ReadFieldType(attribute, depth) is not { } type)
                return null;
            fields.Add(new(name, type, true));
        }
        return fields.Count > 0 ? new(fields) : null;
    }

    private StructuredType? ReadFieldType(CatalogRow field, int depth)
        => field.Bool("field_composite") == true && field.Long("field_type_oid") is { } oid ? Decode(oid, depth + 1)
            : field.Text("field_type_schema") == "pg_catalog" && field.Text("field_type") is { } name
                ? PostgreSqlStructuredLeaf.Decode(name) : null;
}
