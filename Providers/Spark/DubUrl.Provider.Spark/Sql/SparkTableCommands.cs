using System.Security.Cryptography;
using System.Text;
using DubUrl.Querying.Dialects;

namespace DubUrl.Provider.Spark.Sql;

internal static class SparkTableCommands
{
    internal static IEnumerable<SchemaSqlCommand> Plan(SchemaSqlCommand command, bool delta)
    {
        var sql = command.CommandText;
        var tokens = SparkSqlSyntax.Tokenize(sql);
        var start = tokens.FindIndex(token => token.Depth == 0 && token.Is("("));
        var end = tokens.FindIndex(token => token.Depth == 0 && token.Is(")"));
        if (start < 3 || end <= start + 1)
            throw new InvalidOperationException("The CREATE TABLE command does not contain a complete column list.");

        var table = sql[tokens[2].Start..tokens[start].Start].Trim();
        var definitions = new List<string>();
        var checks = new List<(string? Name, string Predicate)>();
        var hasDefault = false;
        var definitionStart = start + 1;
        for (var i = definitionStart; i <= end; i++)
        {
            if (i != end && !(tokens[i].Depth == 1 && tokens[i].Is(",")))
                continue;
            var definition = sql[tokens[definitionStart].Start..tokens[i].Start].Trim();
            var top = tokens.GetRange(definitionStart, i - definitionStart).Where(token => token.Depth == 1).ToArray();
            hasDefault |= top.Skip(1).Where((token, index) => token.Is("DEFAULT") && !top[index].Is("BY")).Any();
            var checkIndex = Array.FindIndex(top, token => token.Is("CHECK"));
            if (checkIndex < 0)
                definitions.Add(definition);
            else
                CollectChecks(sql, top, checkIndex, definitions, checks);
            definitionStart = i + 1;
        }

        if (!delta && checks.Count != 0)
            throw new NotSupportedException("Spark's built-in table provider does not enforce CHECK constraints. Use Databricks Delta.");

        var suffix = sql[tokens[end].End..].TrimEnd().TrimEnd(';');
        var provider = delta ? " USING DELTA" : " USING PARQUET";
        var defaults = delta && hasDefault ? " TBLPROPERTIES ('delta.feature.allowColumnDefaults' = 'supported')" : "";
        yield return command with
        {
            CommandText = $"{sql[..tokens[start].End]}\n    {string.Join(",\n    ", definitions)}\n){provider}{suffix}{defaults};",
        };

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < checks.Count; index++)
        {
            var (name, predicate) = checks[index];
            name ??= GeneratedName(table, index);
            if (!names.Add(name))
                throw new NotSupportedException($"Duplicate check constraint name {name}.");
            yield return new($"ALTER TABLE {table} ADD CONSTRAINT {name} CHECK (coalesce(({predicate}), true));",
                SchemaSqlOperation.CreateTable, command.ObjectName);
        }
    }

    private static void CollectChecks(string sql, SparkSqlSyntax.Token[] tokens, int index, List<string> definitions,
        List<(string? Name, string Predicate)> checks)
    {
        string? name = null;
        if (index == 2 && tokens[0].Is("CONSTRAINT"))
            name = tokens[1].Text;
        else if (index > 0)
            definitions.Add(sql[tokens[0].Start..tokens[index].Start].TrimEnd());

        while (index < tokens.Length)
        {
            if (index + 2 >= tokens.Length || !tokens[index].Is("CHECK")
                || !tokens[index + 1].Is("(") || !tokens[index + 2].Is(")"))
                throw new NotSupportedException("Cannot plan the rendered Spark SQL check constraint.");
            checks.Add((name, sql[tokens[index + 1].End..tokens[index + 2].Start]));
            name = null;
            index += 3;
        }
    }

    private static string GeneratedName(string table, int index)
        => "duburl_check_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{table}:{index}")))[..20].ToLowerInvariant();
}
