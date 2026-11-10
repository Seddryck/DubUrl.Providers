using System.Globalization;
using DubUrl.Provider.Teradata.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Teradata.Schema.Generation;

internal sealed class TeradataSchemaSqlRenderer : StandardSchemaSqlRenderer, IColumnDefaultSqlRenderer
{
    internal static TeradataSchemaSqlRenderer Default { get; } = new();
    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (dialect is not TeradataDialect { VantageVersion: { } version } || version < new Version(17, 20)
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or <= 6 } expression)
            return null;

        var type = context.SqlType.ToUpperInvariant();
        var precision = (expression.FractionalSecondsPrecision ?? 6).ToString(CultureInfo.InvariantCulture);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "TIMESTAMP" => new($"CURRENT_TIMESTAMP({precision})"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CURRENT_DATE"),
            ColumnDefaultKind.CurrentTime when type == "TIME" => new($"CURRENT_TIME({precision})"),
            ColumnDefaultKind.CurrentUser when type.StartsWith("VARCHAR(", StringComparison.Ordinal)
                => new("USER", 128, false),
            _ => null
        };
    }

    public override void ValidateKeyColumn(IDialect dialect, string type, string table, string column)
    {
        if (type.StartsWith("CLOB", StringComparison.Ordinal) || type.StartsWith("BLOB", StringComparison.Ordinal))
            throw new ArgumentException($"Teradata key column '{table}.{column}' uses {type}; supply a bounded VARCHAR/CHAR type and length.");
    }

    public override string RenderColumnType(IDialect dialect, string type, int? length, int precision, int scale, bool nativeType)
    {
        if (type is "VARCHAR" or "CHAR")
        {
            if (length is null or -1)
                throw new ArgumentException($"Teradata {type} requires an explicit bounded length; use an explicit CLOB native type for large non-key text.");
        }
        return base.RenderColumnType(dialect, type, length, precision, scale, nativeType);
    }
}
