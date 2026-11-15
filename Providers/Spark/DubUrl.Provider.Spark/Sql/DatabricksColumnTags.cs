using DubUrl.Discovery;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;

namespace DubUrl.Provider.Spark.Sql;

internal static class DatabricksColumnTags
{
    internal static bool IsLegacyCatalog(string? catalog)
        => string.Equals(catalog, "hive_metastore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(catalog, "spark_catalog", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, string> Read(IEnumerable<CatalogRow> rows, Action<string> diagnose)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(row => row.Text("tag_name"), StringComparer.Ordinal))
        {
            var values = group.Select(row => row.Text("tag_value")).Distinct(StringComparer.Ordinal).ToArray();
            if (group.Key is { Length: > 0 } name && values is [string value])
                annotations.Add(name, value);
            else
                diagnose($"Databricks column tag '{group.Key}' does not have a single text value.");
        }
        return annotations;
    }

    internal static IReadOnlyList<SchemaSqlCommand> Plan(DatabaseObjectName table, string target,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> annotations, Func<string, string> quoteIdentifier)
    {
        if (IsLegacyCatalog(table.Catalog))
            throw new NotSupportedException("Databricks column tags require Unity Catalog.");

        return annotations.Select(column =>
        {
            var tags = column.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                if (pair.Key.Length == 0)
                    throw new NotSupportedException("Databricks column tag names cannot be empty.");
                return $"{SparkStringFormatter.Quote(pair.Key)} = {SparkStringFormatter.Quote(pair.Value)}";
            });
            return new SchemaSqlCommand($"ALTER TABLE {target} ALTER COLUMN {quoteIdentifier(column.Key)} SET TAGS ({string.Join(", ", tags)});",
                SchemaSqlOperation.Comment, $"{table}.{column.Key}");
        }).ToArray();
    }
}
