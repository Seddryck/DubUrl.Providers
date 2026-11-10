using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.MySql.Schema.Generation;

internal sealed class MySqlSchemaSqlRenderer : StandardSchemaSqlRenderer, IUniqueConstraintSqlRenderer, IColumnDefaultSqlRenderer
{
    internal static MySqlSchemaSqlRenderer Default { get; } = new();

    private static readonly Version ExpressionDefaultVersion = new(8, 0, 13);
    private static readonly Version UserDefaultVersion = new(8, 0, 34);
    private static readonly Version MariaDbExpressionDefaultVersion = new(10, 2, 1);
    private static readonly Version MariaDbSequenceVersion = new(10, 3, 0);

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (dialect is not MySqlDialect mysql
            || context.Column.Type == System.Data.DbType.DateTimeOffset
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or 0 } expression)
            return null;

        var mariaDb = mysql.MariaDbVersion >= MariaDbExpressionDefaultVersion;
        var mySql = mysql.InnoDbVersion >= ExpressionDefaultVersion;
        if (!mariaDb && !mySql)
            return null;

        var type = context.SqlType.ToUpperInvariant();
        var userDefaults = mariaDb || mysql.InnoDbVersion >= UserDefaultVersion;
        var text = type.StartsWith("VARCHAR(", StringComparison.Ordinal) || type.StartsWith("CHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentUser when text && userDefaults => new("(CURRENT_USER())", 288, false),
            ColumnDefaultKind.SessionUser when text && userDefaults => new("(SESSION_USER())", 288, false),
            ColumnDefaultKind.CurrentSchema when text => new("(SCHEMA())", 64, true, ColumnDefaultKind.CurrentDatabase),
            ColumnDefaultKind.CurrentDatabase when text => new("(DATABASE())", 64, true, ColumnDefaultKind.CurrentSchema),
            ColumnDefaultKind.NewUuid when text && expression.Representation == UuidRepresentation.CanonicalText
                => new("(UUID())"),
            ColumnDefaultKind.NewUuid when mySql && type is "BINARY(16)" or "VARBINARY(16)"
                && expression.Representation == UuidRepresentation.Binary && expression.ByteOrder == UuidByteOrder.Network
                => new("(UUID_TO_BIN(UUID()))"),
            ColumnDefaultKind.NextSequenceValue when mysql.MariaDbVersion >= MariaDbSequenceVersion
                && expression.Sequence is { Catalog: null, Schema: null, Database: not null } sequence && IsSequenceType(type)
                => new($"(NEXT VALUE FOR {QuoteSequencePart(sequence.Database)}.{QuoteSequencePart(sequence.Name)})"),
            ColumnDefaultKind.CurrentTimestamp when type is "DATETIME" or "TIMESTAMP" => new("CURRENT_TIMESTAMP"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "DATETIME" => new("(UTC_TIMESTAMP())"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("(CURRENT_DATE)"),
            ColumnDefaultKind.CurrentTime when type == "TIME" => new("(CURRENT_TIME)"),
            _ => null
        };
    }

    private static string QuoteSequencePart(string part) => "`" + part.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static bool IsSequenceType(string type)
        => type is "TINYINT" or "SMALLINT" or "INT" or "INTEGER" or "BIGINT" or "DECIMAL"
            || type.StartsWith("DECIMAL(", StringComparison.Ordinal);

    public UniqueConstraintPlan? PlanUniqueConstraint(IDialect dialect, UniqueConstraintContext context)
    {
        var key = context.Constraint;
        return key.NullPolicy == UniqueNullPolicy.NullsDistinct
            || key.Columns.All(column => column.Constraints.Get<NotNullableConstraint>() is not null)
            ? new NativeUniqueConstraintPlan(key.NullPolicy, true)
            : null;
    }

    public override bool SupportsCommentRendering => true;

    public override string RenderColumnType(IDialect dialect, string type, int? length, int precision, int scale, bool nativeType)
    {
        type = ResolveCharacterType(type, length, nativeType);
        if (type is "VARCHAR" or "CHAR") ValidateCharacterDimensions(type, length, precision, scale);
        if (type.Contains("TEXT") || type.Contains("BLOB")) return RenderLargeObjectType(type, length);
        return base.RenderColumnType(dialect, type, length, precision, scale, nativeType);
    }

    private static string ResolveCharacterType(string type, int? length, bool nativeType)
    {
        if (nativeType && (type.Equals("CHAR", StringComparison.OrdinalIgnoreCase) || type.Equals("VARCHAR", StringComparison.OrdinalIgnoreCase)))
            return type.ToUpperInvariant();
        return type == "TEXT" && length > 0 && !nativeType ? "VARCHAR" : type;
    }

    private static void ValidateCharacterDimensions(string type, int? length, int precision, int scale)
    {
        if (length is null or <= 0 || precision != 0 || scale != 0)
            throw new ArgumentException($"MySQL {type} requires an explicit bounded length without numeric precision or scale.");
    }

    private static string RenderLargeObjectType(string type, int? length)
    {
        if (length > 0) throw new ArgumentException($"MySQL {type} does not accept a character length; use a bounded VARCHAR/CHAR native type.");
        return type;
    }
    public override void ValidateKeyColumn(IDialect dialect, string type, string table, string column)
    {
        if (type.Contains("TEXT") || type.Contains("BLOB") || type == "JSON")
            throw new ArgumentException($"MySQL key column '{table}.{column}' uses {type}; supply an explicit bounded VARCHAR/CHAR type and length.");
    }

    public override string RenderColumnComment(IDialect dialect, string description)
        => " COMMENT '" + description.Replace("\\", "\\\\").Replace("'", "''") + "'";

    public override string RenderComment(IDialect dialect, SchemaSqlObjectName table, string? column, string description)
    {
        var name = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        var literal = "'" + description.Replace("\\", "\\\\").Replace("'", "''") + "'";
        // Column COMMENT clauses are emitted with the complete definition in CREATE TABLE.
        return column is null ? $"ALTER TABLE {name} COMMENT = {literal};" : string.Empty;
    }

    public override string RenderIdentity(IDialect dialect, string type, long seed, long increment)
        => seed == 1 && increment == 1 ? $"{type} AUTO_INCREMENT"
            : throw new NotSupportedException($"Dialect '{dialect.GetType().Name}' cannot represent per-column identity seed or increment values.");

    public override string RenderGenerated(IDialect dialect, string type, string expression, bool stored)
        => RenderGeneratedColumn(type, expression, stored);

    public override string RenderRegex(IDialect dialect, string expression, string pattern)
        => $"{expression} REGEXP {pattern}";

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(catalog, "Catalog", dialect);
        RejectQualifier(schema, "Schema", dialect);
        return QuoteIdentifierParts(dialect, database, name);
    }
}
