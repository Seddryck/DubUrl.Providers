using System.Data;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Oracle.Schema.Generation;

internal sealed class OracleSchemaSqlRenderer : StandardSchemaSqlRenderer, IUniqueConstraintSqlRenderer, IColumnDefaultSqlRenderer, IColumnDefinitionSqlRenderer, IStructuredColumnSqlRenderer, IColumnAnnotationSqlRenderer
{
    internal static OracleSchemaSqlRenderer Default { get; } = new();

    public IReadOnlyList<SchemaSqlCommand> PlanColumnAnnotations(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> annotations)
    {
        if (dialect is OracleDialect { ServerVersion: { } version } && !OracleColumnAnnotations.Supports(version))
            throw new NotSupportedException($"Oracle {version} does not support column annotations.");

        var target = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        return annotations.Select(column => new SchemaSqlCommand(
            $"ALTER TABLE {target} MODIFY {RenderIdentifier(dialect, column.Key)} ANNOTATIONS ({OracleColumnAnnotations.Render(column.Value)});",
            SchemaSqlOperation.Comment, $"{table}.{column.Key}")).ToArray();
    }

    public string? RenderStructuredType(IDialect dialect, StructuredColumnTypeContext context)
    {
        if (dialect is not OracleDialect oracle || context.IsKey || context.Column.Generation is not null)
            return null;

        if (context.Type is { Storage: StructuredStorage.Native, Shape: ArrayType or StructType })
            return oracle.ResolveNamedType(context.Type.Shape);
        if (oracle.ServerVersion is not { } version)
            return null;

        var compatible = oracle.CompatibilityVersion ?? version;
        return context.Type switch
        {
            { Storage: StructuredStorage.Json, Shape: JsonType } when version.Major >= 21 && compatible.Major >= 20 => "JSON",
            { Storage: StructuredStorage.Native, Shape: VectorType vector }
                when version >= OracleStructuredTypes.VectorBaseline && compatible >= OracleStructuredTypes.VectorBaseline
                    && context.Column.DefaultValue is null
                    && !context.Column.Constraints.Any(constraint => constraint is CheckConstraint or MembershipCheckConstraint or RegexCheckConstraint)
                => OracleStructuredTypes.RenderVector(vector),
            _ => null
        };
    }

    public ColumnDefinitionPlan? RenderColumnDefinition(IDialect dialect, ColumnDefinitionContext column)
        => column.DefaultExpression is { } value && column.Generation is null
            ? new($"{column.Type} DEFAULT {value}", ColumnDefinitionClauses.Default) : null;

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.Type == DbType.DateTimeOffset
            || context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or 6 } expression)
            return null;

        var type = context.SqlType.ToUpperInvariant();
        var text = type.StartsWith("VARCHAR2(", StringComparison.Ordinal) || type.StartsWith("VARCHAR(", StringComparison.Ordinal)
            || type.StartsWith("CHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "TIMESTAMP" => new("CURRENT_TIMESTAMP"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("TRUNC(CURRENT_DATE)"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "TIMESTAMP" => new("SYS_EXTRACT_UTC(SYSTIMESTAMP)"),
            ColumnDefaultKind.CurrentUser when text => new("SYS_CONTEXT('USERENV','CURRENT_USER')", 128, false),
            ColumnDefaultKind.SessionUser when text => new("SYS_CONTEXT('USERENV','SESSION_USER')", 128, false),
            ColumnDefaultKind.CurrentSchema when text => new("SYS_CONTEXT('USERENV','CURRENT_SCHEMA')", 128, false),
            ColumnDefaultKind.CurrentDatabase when text => new("SYS_CONTEXT('USERENV','DB_NAME')", 8, false),
            ColumnDefaultKind.NewUuid when type == "RAW(16)" && expression.Representation == UuidRepresentation.Binary
                && expression.ByteOrder == UuidByteOrder.Network
                => new("SYS_GUID()"),
            ColumnDefaultKind.NextSequenceValue when dialect is OracleDialect { ServerVersion.Major: >= 12 }
                && expression.Sequence is { Catalog: null, Database: null, Schema: not null } sequence
                && !sequence.Schema.Contains('"') && !sequence.Name.Contains('"') && IsSequenceType(type)
                => new($"{QuoteSequencePart(sequence.Schema)}.{QuoteSequencePart(sequence.Name)}.NEXTVAL"),
            _ => null
        };
    }

    private static string QuoteSequencePart(string part) => "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsSequenceType(string type)
        => type is "NUMBER" or "INTEGER" or "SMALLINT" or "BIGINT" or "DECIMAL"
            || type.StartsWith("NUMBER(", StringComparison.Ordinal) || type.StartsWith("DECIMAL(", StringComparison.Ordinal);

    public UniqueConstraintPlan? PlanUniqueConstraint(IDialect dialect, UniqueConstraintContext context)
    {
        var key = context.Constraint;
        var nonNullable = key.Columns.Count(column => column.Constraints.Get<NotNullableConstraint>() is not null);
        // Oracle ignores all-null tuples and compares partially-null tuples. A required member
        // rules out all-null tuples, while a single-column key cannot be partially null.
        return key.NullPolicy switch
        {
            UniqueNullPolicy.NullsDistinct when key.Columns.Count == 1 || nonNullable == key.Columns.Count
                => new NativeUniqueConstraintPlan(key.NullPolicy, true),
            UniqueNullPolicy.NullsNotDistinct when nonNullable > 0
                => new NativeUniqueConstraintPlan(key.NullPolicy, true),
            _ => null
        };
    }

    public override string RenderColumnType(IDialect dialect, string type, int? length, int precision, int scale, bool nativeType)
    {
        if (!nativeType) return base.RenderColumnType(dialect, type, length, precision, scale, nativeType);
        var parts = type.ToUpperInvariant().Split(' ');
        var characterType = parts[0];
        if (characterType is not ("CHAR" or "VARCHAR" or "VARCHAR2"))
            return base.RenderColumnType(dialect, type, length, precision, scale, nativeType);

        // VARCHAR is currently an Oracle synonym; emit the stable native spelling.
        type = characterType == "CHAR" ? "CHAR" : "VARCHAR2";
        var unit = parts.Length == 2 ? " " + parts[1] : "";
        // Use the portable STANDARD limit. Larger VARCHAR2 values require MAX_STRING_SIZE=EXTENDED.
        var maximum = type == "CHAR" ? 2000 : 4000;
        if (length is null or <= 0 || length > maximum || precision != 0 || scale != 0)
            throw new ArgumentException($"Oracle native {type} requires a length from 1 to {maximum} without numeric precision or scale.");
        return $"{type}({length.Value}{unit})";
    }

    public override string RenderRegex(IDialect dialect, string expression, string pattern)
        => $"REGEXP_LIKE({expression}, {pattern})";
    public override bool SupportsCommentRendering => true;

    public override IReadOnlyList<SchemaSqlCommand> PlanCommands(IDialect dialect, IReadOnlyList<SchemaSqlCommand> statements)
        => statements.Select(statement => statement.IsProceduralBlock ? statement
            : statement with { CommandText = RemoveSqlTerminator(statement.CommandText) }).ToArray();

    // Only the known outer SQL terminator is removed. Literals, comments and block contents are never split.
    private static string RemoveSqlTerminator(string sql)
    {
        var text = sql.TrimEnd();
        return text.EndsWith(';') ? text[..^1] : text;
    }
}
