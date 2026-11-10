using System.Data;
using System.Globalization;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal static class CockroachStructuredTypes
{
    private static readonly Version VectorBaseline = new(24, 2);
    private const int MaxVectorDimensions = 16000;
    private static readonly IReadOnlyDictionary<DbType, string> ScalarTypes = new Dictionary<DbType, string>
    {
        [DbType.Boolean] = "BOOL",
        [DbType.Int16] = "INT2",
        [DbType.Int32] = "INT4",
        [DbType.Int64] = "INT8",
        [DbType.Single] = "FLOAT4",
        [DbType.Double] = "FLOAT8",
        [DbType.String] = "STRING",
        [DbType.Guid] = "UUID",
        [DbType.Date] = "DATE"
    };
    private static readonly IReadOnlyDictionary<string, DbType> CatalogScalars =
        ScalarTypes.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    internal static string? Render(StructuredColumnType type, Version? version)
    {
        if (type is { Storage: StructuredStorage.Json, Shape: JsonType })
            return "JSONB";
        if (type.Storage != StructuredStorage.Native)
            return null;
        return type.Shape switch
        {
            ArrayType { ElementNullable: true, Element: ScalarType scalar } when ScalarTypes.TryGetValue(scalar.Type, out var sql)
                => sql + "[]",
            VectorType { ElementType: DbType.Single, Dimensions: <= MaxVectorDimensions } vector when version >= VectorBaseline
                => FormattableString.Invariant($"VECTOR({vector.Dimensions})"),
            _ => null
        };
    }

    internal static StructuredColumnMetadata? Decode(string family, string? declaration)
    {
        var sql = declaration?.Trim().ToUpperInvariant() ?? "";
        return family.ToLowerInvariant() switch
        {
            "json" or "jsonb" => new(new(new JsonType(), StructuredStorage.Json), MetadataCoverage.Available),
            "array" => Native(ReadArray(sql)),
            "vector" => Native(ReadVector(sql)),
            _ => null
        };
    }

    private static ArrayType? ReadArray(string sql)
    {
        if (!sql.EndsWith("[]", StringComparison.Ordinal))
            return null;
        var element = sql[..^2].TrimEnd();
        return CatalogScalars.TryGetValue(element, out var scalar) ? new(new ScalarType(scalar), true) : null;
    }

    private static VectorType? ReadVector(string sql)
        => sql.StartsWith("VECTOR(", StringComparison.Ordinal) && sql.EndsWith(')')
            && int.TryParse(sql.AsSpan(7, sql.Length - 8).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dimensions)
            && dimensions is > 0 and <= MaxVectorDimensions
            ? new(DbType.Single, dimensions) : null;

    private static StructuredColumnMetadata Native(StructuredType? type)
        => type is null ? new(null, MetadataCoverage.Unrepresentable) : new(new(type, StructuredStorage.Native), MetadataCoverage.Available);
}
