using System.Data;
using System.Globalization;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

/// <summary>Shared qualification and regex syntax within this provider family; no identity/generation is implied.</summary>
internal class PostgreSqlSchemaSqlRenderer : StandardSchemaSqlRenderer
{
    internal static PostgreSqlSchemaSqlRenderer Default { get; } = new();

    public override bool SupportsCommentRendering => true;

    public override string RenderIdentifier(IDialect dialect, string name)
        => name.Contains('"') ? QuoteIdentifier(name) : base.RenderIdentifier(dialect, name);

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(catalog, "Catalog", dialect);
        RejectQualifier(database, "Database", dialect);
        return QuoteIdentifierParts(dialect, schema, name);
    }

    public override string RenderRegex(IDialect dialect, string expression, string pattern)
        => $"{expression} ~ {pattern}";
}

internal class PgsqlSchemaSqlRenderer : PostgreSqlSchemaSqlRenderer, IUniqueConstraintSqlRenderer, IColumnDefaultSqlRenderer, IStructuredColumnSqlRenderer
{
    internal new static PgsqlSchemaSqlRenderer Default { get; } = new();

    public virtual string? RenderStructuredType(IDialect dialect, StructuredColumnTypeContext context)
        => dialect is PgsqlDialect postgres && !context.IsKey && context.Column.Generation is null
            ? context.Type switch
            {
                { Storage: StructuredStorage.Json, Shape: JsonType } => "JSONB",
                { Storage: StructuredStorage.Native, Shape: ArrayType array } when context.Column.DefaultValue is null or DBNull
                    => PostgreSqlArrayType.Render(array, context.Column.Name),
                { Storage: StructuredStorage.Native, Shape: StructType composite } => postgres.ResolveCompositeType(composite),
                _ => null
            }
            : null;

    public virtual ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.Type == DbType.DateTimeOffset
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or <= 6 } expression)
            return null;
        var precision = expression.FractionalSecondsPrecision is int digits
            ? $"({digits.ToString(CultureInfo.InvariantCulture)})" : "";
        var type = context.SqlType.ToUpperInvariant();
        var text = type.StartsWith("VARCHAR(", StringComparison.Ordinal)
            || type.StartsWith("CHAR(", StringComparison.Ordinal) || type.StartsWith("CHARACTER VARYING(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentUser when text => new("CURRENT_USER", 63, false),
            ColumnDefaultKind.SessionUser when text => new("SESSION_USER", 63, false),
            ColumnDefaultKind.CurrentSchema when text => new("CURRENT_SCHEMA", 63, true),
            ColumnDefaultKind.CurrentDatabase when text => new("pg_catalog.current_database()", 63, false),
            ColumnDefaultKind.NewUuid when type == "UUID" && expression.Representation == UuidRepresentation.Native
                && dialect is PgsqlDialect { ServerVersion.Major: >= 13 }
                => new("pg_catalog.gen_random_uuid()", UuidSemantics: new(Version: 4)),
            ColumnDefaultKind.NextSequenceValue when expression.Sequence is { Schema: not null, Database: null, Catalog: null } sequence
                && IsSequenceType(type) => new(RenderSequenceDefault(sequence)),
            ColumnDefaultKind.CurrentTimestamp when type is "TIMESTAMP" or "TIMESTAMP WITHOUT TIME ZONE"
                => new($"LOCALTIMESTAMP{precision}"),
            ColumnDefaultKind.CurrentUtcTimestamp when type is "TIMESTAMP" or "TIMESTAMP WITHOUT TIME ZONE"
                => new($"(CURRENT_TIMESTAMP{precision} AT TIME ZONE 'UTC')"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CURRENT_DATE"),
            ColumnDefaultKind.CurrentTime when type is "TIME" or "TIME WITHOUT TIME ZONE"
                => new($"LOCALTIME{precision}"),
            _ => null
        };
    }

    protected static bool IsSequenceType(string type)
        => type is "SMALLINT" or "INTEGER" or "BIGINT" or "DECIMAL" or "NUMERIC"
            || type.StartsWith("DECIMAL(", StringComparison.Ordinal) || type.StartsWith("NUMERIC(", StringComparison.Ordinal);

    protected static string RenderSequenceDefault(DatabaseObjectName sequence)
    {
        static string Quote(string part) => "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var name = $"{Quote(sequence.Schema!)}.{Quote(sequence.Name)}";
        var literal = name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
        return $"pg_catalog.nextval(E'{literal}'::pg_catalog.regclass)";
    }

    public UniqueConstraintPlan? PlanUniqueConstraint(IDialect dialect, UniqueConstraintContext context)
    {
        if (dialect is not PgsqlDialect)
            return null;
        return context.Constraint.NullPolicy switch
        {
            UniqueNullPolicy.NullsDistinct => new NativeUniqueConstraintPlan(UniqueNullPolicy.NullsDistinct, true),
            UniqueNullPolicy.NullsNotDistinct => new NativeUniqueConstraintPlan(UniqueNullPolicy.NullsNotDistinct, true, "NULLS NOT DISTINCT"),
            _ => null
        };
    }

    public override string RenderIdentity(IDialect dialect, string type, long seed, long increment)
        => $"{type} GENERATED BY DEFAULT AS IDENTITY (START WITH {seed} INCREMENT BY {increment})";

    public override string RenderGenerated(IDialect dialect, string type, string expression, bool stored)
        => stored ? RenderGeneratedColumn(type, expression, true)
            : throw new NotSupportedException("PostgreSQL does not support virtual generated columns.");
}

internal sealed class CockRoachSchemaSqlRenderer : PgsqlSchemaSqlRenderer
{
    internal new static CockRoachSchemaSqlRenderer Default { get; } = new();

    public override string? RenderStructuredType(IDialect dialect, StructuredColumnTypeContext context)
        => dialect is CockRoachDialect cockroach && !context.IsKey && context.Column.Generation is null
            ? CockroachStructuredTypes.Render(context.Type, cockroach.ServerVersion)
            : null;

    public override ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.Type == DbType.DateTimeOffset
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or 6 } expression)
            return null;

        var type = context.SqlType.ToUpperInvariant();
        var text = type.StartsWith("VARCHAR(", StringComparison.Ordinal) || type.StartsWith("CHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "TIMESTAMP" => new("LOCALTIMESTAMP"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CURRENT_DATE"),
            ColumnDefaultKind.CurrentTime when type == "TIME" => new("LOCALTIME"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "TIMESTAMP" => new("timezone('UTC', CURRENT_TIMESTAMP)"),
            ColumnDefaultKind.CurrentUser when text => new("current_user()", 63, false),
            ColumnDefaultKind.SessionUser when text => new("pg_catalog.session_user()", 63, false),
            ColumnDefaultKind.CurrentSchema when text => new("current_schema()", 128, true),
            ColumnDefaultKind.CurrentDatabase when text => new("current_database()", 128, true),
            ColumnDefaultKind.NewUuid when type == "UUID" && expression.Representation == UuidRepresentation.Native
                => new("gen_random_uuid()", UuidSemantics: new(Version: 4)),
            ColumnDefaultKind.NextSequenceValue when expression.Sequence is { Schema: not null, Database: null, Catalog: null } sequence
                && IsSequenceType(type) => new(RenderSequenceDefault(sequence)),
            _ => null
        };
    }

    public override string RenderGenerated(IDialect dialect, string type, string expression, bool stored)
        => stored ? $"{type} AS ({expression}) STORED"
            : throw new NotSupportedException("CockroachDB does not support virtual generated columns.");
}
