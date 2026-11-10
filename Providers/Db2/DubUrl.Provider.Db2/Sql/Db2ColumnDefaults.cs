using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Db2.Sql;

internal sealed class Db2ColumnDefaults : StandardSchemaSqlRenderer, IColumnDefaultSqlRenderer
{
    internal static Db2ColumnDefaults Instance { get; } = new();

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (dialect is not Db2Dialect { LuwVersion: { } version } || version < new Version(11, 5)
            || context.Column.DefaultExpression is not { } expression)
            return null;

        var type = context.SqlType.ToUpperInvariant();
        var text = type.StartsWith("VARCHAR(", StringComparison.Ordinal) || type.StartsWith("CHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "TIMESTAMP" && expression.FractionalSecondsPrecision is null or 6
                => new("CURRENT TIMESTAMP"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CURRENT DATE"),
            ColumnDefaultKind.CurrentTime when type == "TIME" && expression.FractionalSecondsPrecision is null or 0
                => new("CURRENT TIME"),
            ColumnDefaultKind.CurrentUser when text => new("CURRENT USER", 128, false),
            ColumnDefaultKind.SessionUser when text => new("SESSION_USER", 128, false),
            ColumnDefaultKind.CurrentSchema when text => new("CURRENT SCHEMA", 128, false),
            _ => null
        };
    }
}
