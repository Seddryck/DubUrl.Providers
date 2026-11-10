using System.Data;
using DubUrl.Schema;

namespace DubUrl.Provider.Snowflake.Schema.Generation;

internal static class SnowflakeStructuredTypes
{
    internal const int MaxDepth = 64;
    internal const int MaxVectorDimensions = 4096;

    // Snowflake normalizes integer aliases to NUMBER(38, 0). Declare the precision for each portable leaf.
    internal static readonly IReadOnlyDictionary<DbType, string> Scalars = new Dictionary<DbType, string>
    {
        [DbType.Boolean] = "BOOLEAN",
        [DbType.String] = "VARCHAR",
        [DbType.Int16] = "NUMBER(5, 0)",
        [DbType.Int32] = "NUMBER(10, 0)",
        [DbType.Int64] = "NUMBER(19, 0)",
        [DbType.Double] = "FLOAT",
        [DbType.Date] = "DATE"
    };

    internal static string? Render(StructuredColumnType type)
        => type.Storage switch
        {
            StructuredStorage.Json when type.Shape is JsonType => "VARIANT",
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
                return "VARIANT";
            case ArrayType { ElementNullable: { } nullable } array:
                var element = RenderNative(array.Element, depth + 1);
                return element is null ? null : $"ARRAY({element}{Nullability(nullable)})";
            case MapType { Key: ScalarType key, ValueNullable: { } nullable } map when IsMapKey(key.Type):
                var value = RenderNative(map.Value, depth + 1);
                return value is null ? null : $"MAP({Scalars[key.Type]}, {value}{Nullability(nullable)})";
            case StructType structure:
                var fields = new List<string>(structure.Fields.Count);
                foreach (var field in structure.Fields)
                {
                    if (field.Nullable is not { } nullable || !IsFieldName(field.Name)
                        || RenderNative(field.Type, depth + 1) is not { } fieldSql)
                        return null;
                    fields.Add($"{QuoteIdentifier(field.Name)} {fieldSql}{Nullability(nullable)}");
                }
                return $"OBJECT({string.Join(", ", fields)})";
            case VectorType { Dimensions: <= MaxVectorDimensions } vector when depth == 0:
                return vector.ElementType switch
                {
                    DbType.Int32 => FormattableString.Invariant($"VECTOR(INT, {vector.Dimensions})"),
                    DbType.Single => FormattableString.Invariant($"VECTOR(FLOAT, {vector.Dimensions})"),
                    _ => null
                };
            default:
                return null;
        }
    }

    internal static bool IsMapKey(DbType type) => type is DbType.String or DbType.Int16 or DbType.Int32 or DbType.Int64;

    internal static bool IsFieldName(string name)
        => name.Length <= 255 && !string.IsNullOrWhiteSpace(name) && !name.Contains('\0');

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Nullability(bool nullable) => nullable ? "" : " NOT NULL";
}
