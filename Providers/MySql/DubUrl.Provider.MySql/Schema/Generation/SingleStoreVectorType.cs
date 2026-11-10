using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.MySql.Schema.Generation;

internal static class SingleStoreVectorType
{
    // Bound the qualified vector representation independently of index-specific limits.
    internal const int MaxDimensions = 16_384;
    private static readonly IReadOnlyDictionary<DbType, string> Elements = new Dictionary<DbType, string>
    {
        [DbType.SByte] = "I8",
        [DbType.Int16] = "I16",
        [DbType.Int32] = "I32",
        [DbType.Int64] = "I64",
        [DbType.Single] = "F32",
        [DbType.Double] = "F64"
    };
    private static readonly Regex CatalogType = new(@"\Avector\(([0-9]+)(?:,\s*([A-Za-z0-9]+))?\)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    internal static string? Render(VectorType vector)
        => vector.Dimensions <= MaxDimensions && Elements.TryGetValue(vector.ElementType, out var element)
            ? FormattableString.Invariant($"VECTOR({vector.Dimensions}, {element})") : null;

    internal static StructuredColumnMetadata Decode(string sql)
    {
        var match = CatalogType.Match(sql);
        var element = match.Groups[2].Success ? match.Groups[2].Value.ToUpperInvariant() : "F32";
        foreach (var pair in Elements)
        {
            if (pair.Value == element && int.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var size) && size is > 0 and <= MaxDimensions)
                return new(new(new VectorType(pair.Key, size), StructuredStorage.Native), MetadataCoverage.Available);
        }
        return new(null, MetadataCoverage.Unrepresentable);
    }
}
