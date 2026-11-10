using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.MySql.Schema.Generation;

internal static class MariaDbStructuredTypes
{
    // CHECK_CONSTRAINTS.LEVEL identifies the column validation emitted by the JSON alias.
    internal static readonly Version JsonBaseline = new(10, 5, 10);
    private static readonly Version VectorBaseline = new(11, 7, 1);
    private const int MaxDimensions = 16383;

    private static readonly Regex VectorDeclaration = new(@"\Avector\s*\(\s*([0-9]+)\s*\)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string? Render(Version version, StructuredColumnType type)
        => type switch
        {
            { Storage: StructuredStorage.JsonText, Shape: JsonType } when version >= JsonBaseline => "JSON",
            { Storage: StructuredStorage.Native, Shape: VectorType { ElementType: DbType.Single } vector }
                when version >= VectorBaseline && vector.Dimensions <= MaxDimensions
                => FormattableString.Invariant($"VECTOR({vector.Dimensions})"),
            _ => null
        };

    internal static StructuredColumnMetadata DecodeVector(string declaration)
    {
        var match = VectorDeclaration.Match(declaration.Trim());
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            && size is > 0 and <= MaxDimensions
            ? new(new(new VectorType(DbType.Single, size), StructuredStorage.Native), MetadataCoverage.Available)
            : new(null, MetadataCoverage.Unrepresentable);
    }
}
