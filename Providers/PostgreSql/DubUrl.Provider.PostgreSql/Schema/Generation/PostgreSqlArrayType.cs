using System.Data;
using System.Security.Cryptography;
using System.Text;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal static class PostgreSqlArrayType
{
    internal const string RankCheckPrefix = "__duburl_array_rank_";

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

    internal static string? Render(ArrayType array, string name)
    {
        if (array.ElementNullable != true)
            return null;
        var element = array.Element switch
        {
            ScalarType scalar => ScalarTypes.GetValueOrDefault(scalar.Type),
            JsonType { ObjectShape: null } => "jsonb",
            _ => null
        };
        if (element is null)
            return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16)).ToLowerInvariant();
        var column = PostgreSqlSchemaSqlRenderer.QuoteIdentifier(name);
        // PostgreSQL returns NULL rank for NULL and empty arrays. Both satisfy a CHECK.
        return $"pg_catalog.{element}[] CONSTRAINT {RankCheckPrefix}{hash} CHECK (pg_catalog.array_ndims({column}) OPERATOR(pg_catalog.=) 1)";
    }

    internal static StructuredColumnMetadata Decode(int? rank, int? typeModifier, string? elementSchema, string? element,
        bool? defaultCollation, bool checkedRank)
    {
        if (rank != 1 || typeModifier != -1 || elementSchema != "pg_catalog" || element is null || defaultCollation != true)
            return new(null, MetadataCoverage.Unrepresentable);

        StructuredType? leaf = element == "jsonb" ? new JsonType()
            : CatalogTypes.TryGetValue(element, out var type) ? new ScalarType(type) : null;
        return leaf is null ? new(null, MetadataCoverage.Unrepresentable)
            : new(new(new ArrayType(leaf, true), StructuredStorage.Native),
                checkedRank ? MetadataCoverage.Available : MetadataCoverage.Unrepresentable);
    }
}
