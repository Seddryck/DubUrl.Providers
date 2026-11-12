using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal static class SparkColumnDefaults
{
    internal const string UtcTimestamp =
        "TIMESTAMP_NTZ '1970-01-01 00:00:00' + make_dt_interval(0, 0, 0, "
        + "CAST(unix_micros(current_timestamp()) AS DECIMAL(20, 0)) / 1000000)";

    internal static ColumnDefaultPlan? Plan(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.DefaultExpression is not { } expression)
            return null;
        if (expression.FractionalSecondsPrecision is not (null or 6))
            throw new NotSupportedException(
                $"The {expression.Kind} default for '{context.Table}.{context.Column.Name}' supports fractional seconds precision 6. Omit the precision or specify 6.");

        var sqlType = context.SqlType;
        var databricks = dialect is DatabricksSqlDialect;
        var boundedText = sqlType.StartsWith("VARCHAR(", StringComparison.Ordinal)
            || sqlType.StartsWith("CHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTime when sqlType == "TIME" && dialect is DatabricksSqlDialect { SupportsTime: true }
                => new("current_time()"),
            ColumnDefaultKind.CurrentDate when sqlType == "DATE" => new("current_date()"),
            ColumnDefaultKind.CurrentTimestamp when sqlType is "TIMESTAMP" or "TIMESTAMP_NTZ"
                => new(AsTimestamp("current_timestamp()", sqlType)),
            ColumnDefaultKind.CurrentUtcTimestamp when sqlType == "TIMESTAMP_NTZ"
                => new(UtcTimestamp),
            ColumnDefaultKind.CurrentTimestamp when databricks
                => throw new NotSupportedException("Use TIMESTAMP or TIMESTAMP_NTZ for a Databricks session-local timestamp default."),
            ColumnDefaultKind.CurrentUtcTimestamp when databricks
                => throw new NotSupportedException("Use TIMESTAMP_NTZ for a Databricks UTC timestamp default."),
            ColumnDefaultKind.CurrentUser when databricks && boundedText
                => new("current_user()", 320, false, ColumnDefaultKind.SessionUser),
            ColumnDefaultKind.SessionUser when databricks && boundedText
                => new("session_user()", 320, false, ColumnDefaultKind.CurrentUser),
            ColumnDefaultKind.CurrentSchema or ColumnDefaultKind.CurrentDatabase when databricks
                => throw new NotSupportedException("Databricks Delta captures namespace defaults before INSERT. Supply the current schema or database explicitly in the INSERT statement."),
            ColumnDefaultKind.NewUuid => throw new NotSupportedException(
                "Spark SQL rejects nondeterministic UUID column defaults. Generate UUIDs explicitly in the INSERT statement."),
            ColumnDefaultKind.NextSequenceValue => throw new NotSupportedException(
                "Spark SQL does not support external named-sequence defaults. Supply the value in the INSERT statement or use a Delta BIGINT identity column."),
            _ => null,
        };
    }

    private static string AsTimestamp(string sql, string sqlType)
        => sqlType == "TIMESTAMP_NTZ" ? $"CAST({sql} AS TIMESTAMP_NTZ)" : sql;
}
