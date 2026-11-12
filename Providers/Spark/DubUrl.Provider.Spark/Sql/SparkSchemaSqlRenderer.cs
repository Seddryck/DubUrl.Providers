using DubUrl.Querying.Dialects;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class SparkSchemaSqlRenderer : StandardSchemaSqlRenderer, ITableDefinitionSqlRenderer
{
    public static new SparkSchemaSqlRenderer Instance { get; } = new();

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(database, "Database", dialect);
        return ((SparkSqlDialect)dialect).RenderObjectName(name, schema is null ? [] : [schema], catalog);
    }

    public TableDefinitionPlan RenderTableDefinition(IDialect dialect, TableDefinitionContext table)
    {
        var rules = ((SparkSqlDialect)dialect).GetCatalogRules(table.Table.Catalog);
        var seen = new HashSet<string>(rules.Comparer);
        foreach (var column in table.Columns)
        {
            _ = RenderIdentifier(dialect, column);
            if (!seen.Add(column))
                throw new ArgumentException($"Column '{column}' collides under the selected catalog's naming rules.", nameof(table));
        }

        if (table.PrimaryKey is not null || table.UniqueKeys?.Count > 0 || table.Options is not null || table.Indexes?.Count > 0)
            throw new NotSupportedException("Portable Spark SQL table definitions do not support keys, indexes, or physical options.");
        return new("CREATE TABLE", []);
    }

    public string RenderCreateIndex(IDialect dialect, IndexDefinitionContext index)
        => throw new NotSupportedException("Portable Spark SQL index creation is unsupported.");
}
