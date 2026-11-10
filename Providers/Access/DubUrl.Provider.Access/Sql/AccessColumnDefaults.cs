using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Access.Sql;

internal sealed class AccessColumnDefaults : StandardSchemaSqlRenderer, IColumnDefaultSqlRenderer
{
    internal static AccessColumnDefaults Instance { get; } = new();

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (dialect is not MsAccessDialect { UsesAceOleDb: true }
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or 0 } expression)
            return null;
        var type = context.SqlType.ToUpperInvariant();
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "DATETIME" => new("Now()"),
            ColumnDefaultKind.CurrentDate when type is "DATE" or "DATETIME" => new("Date()"),
            ColumnDefaultKind.CurrentTime when type == "DATETIME" => new("Time()"),
            ColumnDefaultKind.NewUuid when type == "GUID" && expression.Representation == UuidRepresentation.Native
                => new("GenGUID()"),
            _ => null
        };
    }
}
