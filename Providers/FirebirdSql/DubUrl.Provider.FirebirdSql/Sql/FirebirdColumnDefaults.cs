using System.Globalization;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.FirebirdSql.Sql;

internal sealed class FirebirdColumnDefaults : StandardSchemaSqlRenderer, IColumnDefaultSqlRenderer, IColumnDefinitionSqlRenderer
{
    internal static FirebirdColumnDefaults Instance { get; } = new();

    public ColumnDefinitionPlan? RenderColumnDefinition(IDialect dialect, ColumnDefinitionContext column)
        => column.DefaultExpression is { } value && column.Generation is null
            ? new($"{column.Type} DEFAULT {value}", ColumnDefinitionClauses.Default) : null;

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (dialect is not FirebirdSqlDialect { ServerVersion.Major: >= 5 }
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or <= 3 } expression)
            return null;

        var precision = expression.FractionalSecondsPrecision is int digits
            ? $"({digits.ToString(CultureInfo.InvariantCulture)})" : "";
        var type = context.SqlType.ToUpperInvariant();
        var text = type.StartsWith("VARCHAR(", StringComparison.Ordinal) || type.StartsWith("CHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "TIMESTAMP" => new($"LOCALTIMESTAMP{precision}"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CURRENT_DATE"),
            ColumnDefaultKind.CurrentTime when type == "TIME" => new($"LOCALTIME{precision}"),
            ColumnDefaultKind.CurrentUser when text => new("CURRENT_USER", 63, false, ColumnDefaultKind.SessionUser),
            ColumnDefaultKind.SessionUser when text => new("CURRENT_USER", 63, false, ColumnDefaultKind.CurrentUser),
            _ => null
        };
    }
}
