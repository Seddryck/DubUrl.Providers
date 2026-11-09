using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Oracle.Schema.Generation;

internal sealed class OracleSchemaSqlRenderer : StandardSchemaSqlRenderer, IUniqueConstraintSqlRenderer
{
    internal static OracleSchemaSqlRenderer Default { get; } = new();

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
