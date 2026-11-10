using System.Data;
using System.Globalization;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal sealed class CrateDbSchemaSqlRenderer : PostgreSqlSchemaSqlRenderer, IColumnDefaultSqlRenderer
{
    internal new static CrateDbSchemaSqlRenderer Default { get; } = new();

    public override string RenderColumnType(IDialect dialect, string type, int? length, int precision, int scale, bool nativeType)
        => type == "TEXT" && length > 0 ? "TEXT" : base.RenderColumnType(dialect, type, length, precision, scale, nativeType);

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.Type == DbType.DateTimeOffset
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or <= 3 } expression)
            return null;

        var type = context.SqlType.ToUpperInvariant();
        var timestamp = type is "TIMESTAMP" or "TIMESTAMP WITHOUT TIME ZONE";
        var clock = expression.FractionalSecondsPrecision is int digits
            ? $"CURRENT_TIMESTAMP({digits.ToString(CultureInfo.InvariantCulture)})" : "CURRENT_TIMESTAMP";
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when timestamp => new($"CAST({clock} AS TIMESTAMP WITHOUT TIME ZONE)"),
            ColumnDefaultKind.CurrentUtcTimestamp when timestamp => new($"timezone('UTC', {clock})"),
            ColumnDefaultKind.CurrentDate when type == "TIMESTAMP WITHOUT TIME ZONE"
                => new("CAST(CURRENT_DATE AS TIMESTAMP WITHOUT TIME ZONE)"),
            ColumnDefaultKind.CurrentTime when type == "BIGINT"
                => new($"((CAST({clock} AS BIGINT) % 86400000) * 1000)"),
            ColumnDefaultKind.CurrentUser when type == "TEXT" => new("CURRENT_USER", 1, false, ColumnDefaultKind.SessionUser),
            ColumnDefaultKind.SessionUser when type == "TEXT" => new("SESSION_USER", 1, false, ColumnDefaultKind.CurrentUser),
            ColumnDefaultKind.CurrentSchema when type == "TEXT" => new("CURRENT_SCHEMA", 1, false),
            ColumnDefaultKind.CurrentDatabase when type == "TEXT" => new("pg_catalog.current_database()", 5, false),
            _ => null
        };
    }
}
