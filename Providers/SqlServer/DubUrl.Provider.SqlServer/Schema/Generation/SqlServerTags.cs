using System.Text;
using DubUrl.Discovery;

namespace DubUrl.Provider.SqlServer.Schema.Generation;

internal static class SqlServerTags
{
    internal const string Prefix = "dbu.tag.v1.";
    private const int PropertyNameLimit = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static bool IsReserved(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    // Hex avoids collation-dependent conflation of case, accents, and trailing spaces in tag keys.
    internal static string Encode(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0'))
            throw new NotSupportedException("SQL Server tag names must be nonblank and cannot contain a null character.");
        try
        {
            var name = Prefix + Convert.ToHexString(StrictUtf8.GetBytes(key));
            return name.Length <= PropertyNameLimit ? name
                : throw new NotSupportedException($"SQL Server tag '{key}' exceeds {(PropertyNameLimit - Prefix.Length) / 2} UTF-8 bytes.");
        }
        catch (EncoderFallbackException)
        {
            throw new NotSupportedException("SQL Server tag names must contain valid Unicode.");
        }
    }

    internal static IReadOnlyDictionary<string, string> Read(IEnumerable<CatalogRow> rows, out bool complete)
    {
        complete = true;
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = Decode(row.Text("property_name")!);
            var value = row.Text("property_value");
            if (key is null || value is not { Length: <= TSqlSchemaSqlRenderer.MaxExtendedPropertyValueLength }
                || !tags.TryAdd(key, value))
                complete = false;
        }
        return tags;
    }

    private static string? Decode(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length > PropertyNameLimit)
            return null;
        try
        {
            var key = StrictUtf8.GetString(Convert.FromHexString(name[Prefix.Length..]));
            return !string.IsNullOrWhiteSpace(key) && !key.Contains('\0') && Encode(key) == name ? key : null;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }
}
