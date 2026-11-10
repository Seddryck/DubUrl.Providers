using DubUrl.Discovery;
using DubUrl.Provider.Oracle.Schema.Generation;
using DubUrl.Schema;

namespace DubUrl.Provider.Oracle.Discovery;

internal sealed record OracleNamedTypeMetadata(StructuredColumnMetadata Structured, int? Capacity = null);

internal sealed class OracleNamedTypeCatalog(IEnumerable<CatalogRow> members)
{
    internal const string Sql = """
        SELECT t.owner AS type_owner, t.type_name, t.typecode AS kind, t.attributes AS attribute_count,
          CASE WHEN t.predefined = 'NO' AND t.incomplete = 'NO' AND t.final = 'YES' AND t.instantiable = 'YES'
            AND t.supertype_name IS NULL AND t.methods = 0 THEN 1 ELSE 0 END AS supported_type,
          a.attr_no AS ordinal, a.attr_name AS field_name, a.inherited,
          v.coll_type AS collection_kind, v.upper_bound AS capacity, v.nulls_stored,
          COALESCE(a.attr_type_owner, v.elem_type_owner) AS member_owner,
          COALESCE(a.attr_type_name, v.elem_type_name) AS member_type,
          COALESCE(a.attr_type_mod, v.elem_type_mod) AS member_modifier,
          COALESCE(a.precision, v.precision) AS member_precision,
          COALESCE(a.scale, v.scale) AS member_scale,
          COALESCE(a.length, v.length) AS member_length,
          COALESCE(a.char_used, v.char_used) AS member_char_used
        FROM all_types t
        JOIN all_users u ON u.username = t.owner AND u.oracle_maintained = 'N'
        LEFT JOIN all_type_attrs a ON a.owner = t.owner AND a.type_name = t.type_name
        LEFT JOIN all_coll_types v ON v.owner = t.owner AND v.type_name = t.type_name
        WHERE t.typecode IN ('OBJECT', 'COLLECTION')
        """;

    private readonly ILookup<(string? Owner, string? Name), CatalogRow> types =
        members.ToLookup(row => (row.Text("type_owner"), row.Text("type_name")));
    private readonly Dictionary<(string Owner, string Name, int Depth), StructuredType?> decoded = new();

    internal OracleNamedTypeMetadata Decode(string owner, string name, string? modifier)
    {
        var rows = types[(owner, name)];
        var capacity = rows.FirstOrDefault()?.Int("capacity");
        var shape = modifier is null ? Decode(owner, name, 0) : null;
        return shape is null
            ? new(new(null, MetadataCoverage.Unrepresentable), capacity)
            : new(new(new(shape, StructuredStorage.Native), MetadataCoverage.Available), capacity);
    }

    private StructuredType? Decode(string owner, string name, int depth)
    {
        if (depth >= OracleNamedType.MaxDepth)
            return null;
        var key = (owner, name, depth);
        if (decoded.TryGetValue(key, out var cached))
            return cached;
        var rows = types[(owner, name)].ToArray();
        StructuredType? shape = null;
        if (rows.Length > 0 && rows[0].Bool("supported_type") == true)
            shape = rows[0].Text("kind") == "OBJECT" ? ReadObject(rows, depth) : ReadVarray(rows, depth);
        decoded.Add(key, shape);
        return shape;
    }

    private StructType? ReadObject(CatalogRow[] rows, int depth)
    {
        if (rows[0].Int("attribute_count") != rows.Length)
            return null;
        var fields = new List<StructField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(row => row.Int("ordinal")))
        {
            if (row.Text("field_name") is not { } name || !OracleNamedType.ValidIdentifier(name) || !names.Add(name)
                || row.Text("inherited") != "NO" || ReadMember(row, depth) is not { } type)
                return null;
            fields.Add(new(name, type, true));
        }
        return new(fields);
    }

    private ArrayType? ReadVarray(CatalogRow[] rows, int depth)
        => rows.Length == 1 && rows[0] is { } row && row.Text("collection_kind") == "VARYING ARRAY" && row.Int("capacity") > 0
            && row.Text("nulls_stored") == "YES" && ReadMember(row, depth) is { } item
            ? new(item, true) : null;

    private StructuredType? ReadMember(CatalogRow row, int depth)
    {
        if (row.Text("member_modifier") is not null)
            return null;
        if (row.Text("member_owner") is { } owner && row.Text("member_type") is { } type)
            return Decode(owner, type, depth + 1);
        return OracleNamedType.DecodeScalar(row.Text("member_type"), row.Int("member_precision"), row.Int("member_scale"),
            row.Int("member_length"), row.Text("member_char_used"));
    }
}
