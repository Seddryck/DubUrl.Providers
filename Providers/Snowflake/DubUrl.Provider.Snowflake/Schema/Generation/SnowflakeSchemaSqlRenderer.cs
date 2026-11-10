using System.Globalization;
using DubUrl.Provider.Snowflake.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Snowflake.Schema.Generation;

internal sealed class SnowflakeSchemaSqlRenderer : StandardSchemaSqlRenderer, IColumnDefaultSqlRenderer
{
    internal static SnowflakeSchemaSqlRenderer Default { get; } = new();
    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (dialect is not SnowflakeDialect { UsesStandardTableDefaults: true }
            || context.Column.Type == System.Data.DbType.DateTimeOffset
            || context.Column.DefaultExpression is not { } expression)
            return null;

        var type = context.SqlType.ToUpperInvariant();
        var text = type.StartsWith("VARCHAR(", StringComparison.Ordinal) || type.StartsWith("CHAR(", StringComparison.Ordinal);
        var precision = expression.FractionalSecondsPrecision?.ToString(CultureInfo.InvariantCulture);
        var timestamp = $"CURRENT_TIMESTAMP({precision})";
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "TIMESTAMP_NTZ" => new($"CAST({timestamp} AS TIMESTAMP_NTZ)"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "TIMESTAMP_NTZ"
                => new($"CAST(CONVERT_TIMEZONE('UTC', {timestamp}) AS TIMESTAMP_NTZ)"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CURRENT_DATE()"),
            ColumnDefaultKind.CurrentTime when type == "TIME" => new($"CURRENT_TIME({precision})"),
            ColumnDefaultKind.CurrentUser when text => new("CURRENT_USER()", 255, false, ColumnDefaultKind.SessionUser),
            ColumnDefaultKind.SessionUser when text => new("CURRENT_USER()", 255, false, ColumnDefaultKind.CurrentUser),
            ColumnDefaultKind.CurrentSchema when text => new("CURRENT_SCHEMA()", 255, true),
            ColumnDefaultKind.CurrentDatabase when text => new("CURRENT_DATABASE()", 255, true),
            ColumnDefaultKind.NewUuid when text && expression.Representation == UuidRepresentation.CanonicalText
                => new("UUID_STRING()", UuidSemantics: new(Version: 4)),
            ColumnDefaultKind.NextSequenceValue when expression.Sequence is { Catalog: null, Schema: not null } sequence
                && IsSequenceType(type) => new($"{SequenceName(sequence)}.NEXTVAL"),
            _ => null
        };
    }

    private static string SequenceName(DatabaseObjectName sequence)
        => string.Join('.', new[] { sequence.Database, sequence.Schema, sequence.Name }.OfType<string>()
            .Select(part => "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));

    private static bool IsSequenceType(string type)
        => type is "SMALLINT" or "INTEGER" or "BIGINT" or "DECIMAL" or "NUMBER" or "NUMERIC"
            || type.StartsWith("DECIMAL(", StringComparison.Ordinal) || type.StartsWith("NUMBER(", StringComparison.Ordinal);

    public override bool SupportsCommentRendering => true;

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(catalog, "Catalog", dialect);
        return QuoteIdentifierParts(dialect, database, schema, name);
    }

    public override string RenderComment(IDialect dialect, SchemaSqlObjectName table, string? column, string description)
        => base.RenderComment(dialect, table, column, description.Replace("\\", "\\\\"));
}
