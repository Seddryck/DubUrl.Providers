using DubUrl.Discovery;
using DubUrl.Providers;

namespace DubUrl.Provider.Spark.Discovery;

internal static class SparkTableProperties
{
    internal static IReadOnlyDictionary<string, string> Read(IEnumerable<CatalogRow> rows, Func<string, bool> exclude)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Text("key") is { } key && row.Text("value") is { } value && !exclude(key))
                annotations[key] = value;
        }
        return annotations;
    }

    internal static bool IsDelta(string key) => key.StartsWith("delta.", StringComparison.OrdinalIgnoreCase);

    internal static bool IsReserved(string key) =>
        ColumnAnnotationProperty.IsReserved(key)
        || key.ToLowerInvariant() is "external" or "location" or "owner" or "provider" or "comment"
            or "transient_lastddltime" or "last_modified_time" or "last_modified_user"
            or "numfiles" or "numpartitions" or "numrows" or "rawdatasize" or "totalsize"
        || key.StartsWith("spark.sql.", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("option.", StringComparison.OrdinalIgnoreCase);
}
