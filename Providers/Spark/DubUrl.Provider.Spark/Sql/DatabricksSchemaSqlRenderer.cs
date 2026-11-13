using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class DatabricksSchemaSqlRenderer : SparkSchemaSqlRenderer, IColumnAnnotationSqlRenderer
{
    internal static new DatabricksSchemaSqlRenderer Instance { get; } = new();

    public IReadOnlyList<SchemaSqlCommand> PlanColumnAnnotations(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> annotations)
    {
        var target = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        return DatabricksColumnTags.Plan(table, target, annotations, column => RenderIdentifier(dialect, column));
    }
}
