using System.Data;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal sealed class QuestDbSchemaSqlRenderer : PostgreSqlSchemaSqlRenderer, IStructuredColumnSqlRenderer
{
    internal new static QuestDbSchemaSqlRenderer Default { get; } = new();
    private static readonly Version ArrayBaseline = new(9, 0);

    public string? RenderStructuredType(IDialect dialect, StructuredColumnTypeContext context)
        => dialect is QuestDbDialect quest && quest.ServerVersion >= ArrayBaseline
            && context is { IsKey: false, Nullable: not false, Column.Generation: null, Column.DefaultValue: null }
            && context.Type is { Storage: StructuredStorage.Native, Shape: ArrayType { Element: ScalarType { Type: DbType.Double }, ElementNullable: true } }
            ? "DOUBLE[]" : null;
}
