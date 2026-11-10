using System.Data;
using DubUrl.Schema;

namespace DubUrl.Provider.Oracle.Schema.Generation;

internal static class OracleStructuredTypes
{
    internal static readonly Version VectorBaseline = new(23, 4);
    internal const int MaxVectorDimensions = 65_535;
    internal static readonly IReadOnlyDictionary<DbType, string> VectorFormats = new Dictionary<DbType, string>
    {
        [DbType.SByte] = "INT8",
        [DbType.Single] = "FLOAT32",
        [DbType.Double] = "FLOAT64"
    };

    internal static string? RenderVector(VectorType vector)
        => vector.Dimensions <= MaxVectorDimensions && VectorFormats.TryGetValue(vector.ElementType, out var format)
            ? FormattableString.Invariant($"VECTOR({vector.Dimensions}, {format})") : null;
}
