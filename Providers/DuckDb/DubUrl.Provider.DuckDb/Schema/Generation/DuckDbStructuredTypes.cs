using System.Data;
using DubUrl.Schema;

namespace DubUrl.Provider.DuckDb.Schema.Generation;

internal static class DuckDbStructuredTypes
{
    // Keep the supported leaf types reversible. Faceted and custom types cannot be represented.
    internal static readonly IReadOnlyDictionary<DbType, string> Scalars = new Dictionary<DbType, string>
    {
        [DbType.Boolean] = "BOOLEAN",
        [DbType.SByte] = "TINYINT",
        [DbType.Byte] = "UTINYINT",
        [DbType.Int16] = "SMALLINT",
        [DbType.UInt16] = "USMALLINT",
        [DbType.Int32] = "INTEGER",
        [DbType.UInt32] = "UINTEGER",
        [DbType.Int64] = "BIGINT",
        [DbType.UInt64] = "UBIGINT",
        [DbType.Single] = "FLOAT",
        [DbType.Double] = "DOUBLE",
        [DbType.String] = "VARCHAR",
        [DbType.Guid] = "UUID",
        [DbType.Date] = "DATE",
        [DbType.Binary] = "BLOB"
    };

    internal const int MaxDepth = 64;

    internal static string? Render(StructuredColumnType type)
        => type.Storage switch
        {
            StructuredStorage.Json when type.Shape is JsonType => "JSON",
            StructuredStorage.JsonText when type.Shape is JsonType => "VARCHAR",
            StructuredStorage.Native => RenderNative(type.Shape, 0),
            _ => null
        };

    private static string? RenderNative(StructuredType type, int depth)
    {
        if (depth >= MaxDepth)
            return null;

        switch (type)
        {
            case ScalarType scalar:
                return Scalars.GetValueOrDefault(scalar.Type);
            case JsonType { ObjectShape: null } when depth > 0:
                return "JSON";
            case ArrayType { ElementNullable: true } list:
                var item = RenderNative(list.Element, depth + 1);
                return item is null ? null : item + "[]";
            case MapType { Key: ScalarType key, ValueNullable: true } map:
                var keySql = Scalars.GetValueOrDefault(key.Type);
                var value = RenderNative(map.Value, depth + 1);
                return keySql is null || value is null ? null : $"MAP({keySql}, {value})";
            case StructType structure:
                var fields = new List<string>(structure.Fields.Count);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in structure.Fields)
                {
                    if (field.Nullable != true || !names.Add(FoldName(field.Name)))
                        return null;
                    var fieldSql = RenderNative(field.Type, depth + 1);
                    if (fieldSql is null)
                        return null;
                    var escapedName = field.Name.Replace("\"", "\"\"", StringComparison.Ordinal);
                    fields.Add($"\"{escapedName}\" {fieldSql}");
                }
                return $"STRUCT({string.Join(", ", fields)})";
            default:
                return null;
        }
    }

    internal static string FoldName(string name)
        => new(name.Select(character => character is >= 'A' and <= 'Z'
            ? char.ToLowerInvariant(character) : character).ToArray());
}
