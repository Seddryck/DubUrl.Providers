using System.Data;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal static class PostgreSqlStructuredLeaf
{
    private static readonly IReadOnlyDictionary<DbType, string> ScalarTypes = new Dictionary<DbType, string>
    {
        [DbType.Boolean] = "bool",
        [DbType.Int16] = "int2",
        [DbType.Int32] = "int4",
        [DbType.Int64] = "int8",
        [DbType.Single] = "float4",
        [DbType.Double] = "float8",
        [DbType.String] = "text",
        [DbType.Binary] = "bytea",
        [DbType.Guid] = "uuid",
        [DbType.Date] = "date"
    };

    private static readonly IReadOnlyDictionary<string, DbType> CatalogTypes =
        ScalarTypes.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);

    internal static string? Render(StructuredType shape)
        => shape switch
        {
            ScalarType scalar => ScalarTypes.GetValueOrDefault(scalar.Type),
            JsonType { ObjectShape: null } => "jsonb",
            _ => null
        };

    internal static StructuredType? Decode(string name)
        => name == "jsonb" ? new JsonType() : CatalogTypes.TryGetValue(name, out var type) ? new ScalarType(type) : null;
}
