using System.Runtime.CompilerServices;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class DatabricksSchemaSqlRenderer : SparkSchemaSqlRenderer, IColumnAnnotationSqlRenderer,
    ITableTagSqlRenderer, IColumnTagSqlRenderer
{
    internal static new DatabricksSchemaSqlRenderer Instance { get; } = new();

    // Keep the structured assignments with the commands until PlanCommands can see both mappings.
    private readonly ConditionalWeakTable<SchemaSqlCommand, ColumnTagAssignment> columnAssignments = new();

    public IReadOnlyList<SchemaSqlCommand> PlanTableTags(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, string> tags)
    {
        DatabricksTags.EnsureSupported(table);
        var target = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        return [new($"ALTER TABLE {target} SET TAGS ({DatabricksTags.Format(tags)});", SchemaSqlOperation.Comment, table.ToString())];
    }

    public IReadOnlyList<SchemaSqlCommand> PlanColumnAnnotations(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> annotations)
        => PlanColumnAssignments(dialect, table, annotations, legacy: true);

    public IReadOnlyList<SchemaSqlCommand> PlanColumnTags(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tags)
        => PlanColumnAssignments(dialect, table, tags, legacy: false);

    private IReadOnlyList<SchemaSqlCommand> PlanColumnAssignments(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> values, bool legacy)
    {
        DatabricksTags.EnsureSupported(table);
        var target = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        return values.Where(column => column.Value.Count > 0).Select(column =>
        {
            var prefix = $"ALTER TABLE {target} ALTER COLUMN {RenderIdentifier(dialect, column.Key)} SET TAGS";
            var assignment = new ColumnTagAssignment(prefix, new Dictionary<string, string>(column.Value, StringComparer.Ordinal), legacy);
            var command = new SchemaSqlCommand(assignment.Render(assignment.Tags), SchemaSqlOperation.Comment, $"{table}.{column.Key}");
            columnAssignments.Add(command, assignment);
            return command;
        }).ToArray();
    }

    public override IReadOnlyList<SchemaSqlCommand> PlanCommands(IDialect dialect, IReadOnlyList<SchemaSqlCommand> statements)
    {
        var legacy = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var command in statements)
            if (columnAssignments.TryGetValue(command, out var assignment) && assignment.Legacy)
                legacy.Add(assignment.Prefix, assignment.Tags);

        return legacy.Count == 0 ? base.PlanCommands(dialect, statements)
            : base.PlanCommands(dialect, RemoveRepeatedTags(statements, legacy).ToArray());
    }

    private IEnumerable<SchemaSqlCommand> RemoveRepeatedTags(IReadOnlyList<SchemaSqlCommand> statements,
        Dictionary<string, IReadOnlyDictionary<string, string>> legacy)
    {
        foreach (var command in statements)
        {
            if (!columnAssignments.TryGetValue(command, out var assignment) || assignment.Legacy
                || !legacy.TryGetValue(assignment.Prefix, out var annotations))
            {
                yield return command;
                continue;
            }

            var remaining = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in assignment.Tags)
            {
                if (!annotations.TryGetValue(key, out var oldValue))
                    remaining.Add(key, value);
                else if (value != oldValue)
                    throw new NotSupportedException($"Databricks column '{command.ObjectName}' declares different values for tag '{key}' and its legacy annotation.");
            }
            if (remaining.Count != 0)
                yield return command with { CommandText = assignment.Render(remaining) };
        }
    }

    private sealed record ColumnTagAssignment(string Prefix, IReadOnlyDictionary<string, string> Tags, bool Legacy)
    {
        public string Render(IReadOnlyDictionary<string, string> tags) => $"{Prefix} ({DatabricksTags.Format(tags)});";
    }
}
