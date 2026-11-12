using System.Security.Cryptography;
using System.Text;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal static class SparkCheckCommands
{
    internal static SchemaSqlCommand[] Plan(IDialect dialect, DatabaseObjectName table, string sqlName,
        IReadOnlyList<SchemaCheckDefinition> checks)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return checks.Select((check, index) =>
        {
            var name = check.Name ?? GeneratedName(sqlName, index);
            if (!names.Add(name))
                throw new NotSupportedException($"Duplicate check constraint name '{name}' on '{table}'.");
            var quoted = dialect.SchemaSqlRenderer.RenderIdentifier(dialect, name);
            return new SchemaSqlCommand($"ALTER TABLE {sqlName} ADD CONSTRAINT {quoted} CHECK (coalesce(({check.Predicate}), true));",
                SchemaSqlOperation.CreateTable, table.ToString());
        }).ToArray();
    }

    private static string GeneratedName(string table, int index)
        => "duburl_check_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{table}:{index}"))))[..20].ToLowerInvariant();
}
