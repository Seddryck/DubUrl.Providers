using System.Data;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal static class CrateDbStructuredTypes
{
    private static readonly Version VectorBaseline = new(5, 5);
    internal const int MaxVectorDimensions = 2048;
    internal const int MaxDepth = 32;

    private static readonly IReadOnlyDictionary<DbType, string> Scalars = new Dictionary<DbType, string>
    {
        [DbType.Boolean] = "BOOLEAN",
        [DbType.Int16] = "SMALLINT",
        [DbType.Int32] = "INTEGER",
        [DbType.Int64] = "BIGINT",
        [DbType.Single] = "REAL",
        [DbType.Double] = "DOUBLE PRECISION",
        [DbType.String] = "TEXT"
    };

    private static readonly IReadOnlyDictionary<string, DbType> CatalogScalars =
        Scalars.ToDictionary(pair => pair.Value.ToLowerInvariant(), pair => pair.Key, StringComparer.Ordinal);

    internal static string? Render(StructuredColumnType type, Version? version)
        => type.Storage == StructuredStorage.Native ? Render(type.Shape, version, 0) : null;

    private static string? Render(StructuredType shape, Version? version, int depth)
    {
        if (depth >= MaxDepth)
            return null;
        switch (shape)
        {
            case ScalarType scalar:
                return Scalars.GetValueOrDefault(scalar.Type);
            case VectorType { ElementType: DbType.Single, Dimensions: <= MaxVectorDimensions } vector
                when depth == 0 && version >= VectorBaseline:
                return FormattableString.Invariant($"FLOAT_VECTOR({vector.Dimensions})");
            case ArrayType { ElementNullable: true, Element: not ScalarType { Type: DbType.String } } array:
                return Render(array.Element, version, depth + 1) is { } element ? $"ARRAY({element})" : null;
            case StructType structure:
                var fields = new List<string>(structure.Fields.Count);
                foreach (var field in structure.Fields)
                {
                    // CrateDB applies nested NOT NULL even when an ancestor object is NULL.
                    if (field.Nullable != true || !IsFieldName(field.Name)
                        || Render(field.Type, version, depth + 1) is not { } sql)
                        return null;
                    fields.Add($"\"{field.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\" {sql}");
                }
                return $"OBJECT(STRICT) AS ({string.Join(", ", fields)})";
            default:
                return null;
        }
    }

    internal static bool IsFieldName(string name) => !string.IsNullOrWhiteSpace(name) && !name.Contains('\0');

    internal static ScalarType? ReadScalar(string name)
        => CatalogScalars.TryGetValue(name, out var type) ? new(type) : null;
}
