using DubUrl.Discovery;
using DubUrl.Schema;

namespace DubUrl.Provider.Spark.Sql;

internal static class DatabricksTags
{
    internal static bool IsLegacyCatalog(string? catalog)
        => string.Equals(catalog, "hive_metastore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(catalog, "spark_catalog", StringComparison.OrdinalIgnoreCase);

    internal static void EnsureSupported(DatabaseObjectName table)
    {
        if (IsLegacyCatalog(table.Catalog))
            throw new NotSupportedException("Databricks tags require Unity Catalog.");
    }

    internal static IReadOnlyDictionary<string, string> Read(IEnumerable<CatalogRow> rows, out bool complete)
    {
        complete = true;
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(row => row.Text("tag_name"), StringComparer.Ordinal))
        {
            var values = group.Select(row => row.Text("tag_value")).Distinct(StringComparer.Ordinal).ToArray();
            if (!string.IsNullOrWhiteSpace(group.Key) && values is [string value])
                tags.Add(group.Key, value);
            else
                complete = false;
        }
        return tags;
    }

    internal static string Format(IReadOnlyDictionary<string, string> tags)
        => string.Join(", ", tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new NotSupportedException("Databricks tag names cannot be blank.");
            return $"{SparkStringFormatter.Quote(pair.Key)} = {SparkStringFormatter.Quote(pair.Value)}";
        }));
}
