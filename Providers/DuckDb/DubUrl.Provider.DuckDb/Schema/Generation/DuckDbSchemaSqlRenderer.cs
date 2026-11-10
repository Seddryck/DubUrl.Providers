using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.DuckDb.Schema.Generation;

/// <summary>DuckDB uses a dedicated sequence and nextval default for each portable identity column.</summary>
internal sealed class DuckDbSchemaSqlRenderer : StandardSchemaSqlRenderer, ISequenceIdentitySqlRenderer, IUniqueConstraintSqlRenderer, IColumnDefaultSqlRenderer, IStructuredColumnSqlRenderer
{
    internal static DuckDbSchemaSqlRenderer Default { get; } = new();

    internal const string UtcTimestamp = "timezone('UTC', CURRENT_TIMESTAMP)";

    public string? RenderStructuredType(IDialect dialect, StructuredColumnTypeContext context)
        => context.IsKey || context.Column.Generation is not null ? null
            : DuckDbStructuredTypes.Render(context.Type);

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or 6 } expression)
            return null;
        var type = context.SqlType.ToUpperInvariant();
        var text = type.StartsWith("VARCHAR(", StringComparison.Ordinal) || type.StartsWith("CHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            // DuckDB does not enforce a declared VARCHAR length.
            ColumnDefaultKind.CurrentUser when text => new("CURRENT_USER", 1, false, ColumnDefaultKind.SessionUser),
            ColumnDefaultKind.SessionUser when text => new("SESSION_USER", 1, false, ColumnDefaultKind.CurrentUser),
            ColumnDefaultKind.CurrentSchema when text => new("current_schema()", 1, false),
            ColumnDefaultKind.CurrentDatabase when text => new("current_database()", 1, false),
            ColumnDefaultKind.NewUuid when type == "UUID" && expression.Representation == UuidRepresentation.Native
                => new("uuid()", UuidSemantics: new(Version: 4)),
            ColumnDefaultKind.NewUuid when text && expression.Representation == UuidRepresentation.CanonicalText
                => new("CAST(uuid() AS VARCHAR)", UuidSemantics: new(Version: 4)),
            ColumnDefaultKind.NextSequenceValue when expression.Sequence is { Schema: not null, Catalog: null } sequence
                && IsSequenceType(type) && CanResolveSequenceName(sequence) => new(RenderSequenceDefault(dialect, sequence)),
            ColumnDefaultKind.CurrentTimestamp when context.Column.Type != DbType.DateTimeOffset && type == "TIMESTAMP"
                => new("CURRENT_TIMESTAMP"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "TIMESTAMP" => new(UtcTimestamp),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CURRENT_DATE"),
            ColumnDefaultKind.CurrentTime when type == "TIME" => new("LOCALTIME"),
            _ => null
        };
    }

    // DuckDB's nextval(string) qualified-name parser cannot encode an embedded double quote.
    private static bool CanResolveSequenceName(DatabaseObjectName name)
        => !name.Name.Contains('"') && name.Schema?.Contains('"') != true && name.Database?.Contains('"') != true;

    private string RenderSequenceDefault(IDialect dialect, DatabaseObjectName sequence)
    {
        var name = RenderObjectName(dialect, sequence.Catalog, sequence.Database, sequence.Schema, sequence.Name);
        return $"nextval('{name.Replace("'", "''", StringComparison.Ordinal)}')";
    }

    private static bool IsSequenceType(string type)
        => type is "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "DECIMAL"
            || type.StartsWith("DECIMAL(", StringComparison.Ordinal);

    public UniqueConstraintPlan? PlanUniqueConstraint(IDialect dialect, UniqueConstraintContext context)
    {
        var key = context.Constraint;
        if (key.NullPolicy != UniqueNullPolicy.NullsDistinct
            && key.Columns.Any(column => column.Constraints.Get<NotNullableConstraint>() is null))
            return null;

        // DuckDB generates native constraint names. A separate index preserves a custom name,
        // but DuckDB only accepts a native UNIQUE constraint as a foreign-key target.
        var table = context.Table.Identity;
        var generatedName = $"{table.Name}_{string.Join('_', key.Columns.Select(column => DuckDbStructuredTypes.FoldName(column.Name)))}_key";
        if (key.Name is null || key.Name == generatedName)
            return new NativeUniqueConstraintPlan(key.NullPolicy, true);

        return new UniqueIndexPlan(key.NullPolicy, false,
            [new(new DatabaseObjectName(key.Name, table.Schema, table.Database, table.Catalog))]);
    }

    public override bool RenderForeignKeysInline(IDialect dialect) => true;
    public override bool RequiresForeignKeyDependencyOrder => true;

    public override bool SupportsCommentRendering => true;

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(catalog, "Catalog", dialect);
        return QuoteIdentifierParts(dialect, database, schema, name);
    }

    public override string RenderGenerated(IDialect dialect, string type, string expression, bool stored)
        => !stored ? RenderGeneratedColumn(type, expression, false)
            : throw new NotSupportedException("DuckDB does not support stored generated columns.");

    public override string RenderRegex(IDialect dialect, string expression, string pattern)
        => $"regexp_matches({expression}, {pattern})";

    public override string RenderCreateIndexName(IDialect dialect, SchemaSqlObjectName index, SchemaSqlObjectName table)
    {
        _ = RenderObjectName(dialect, index.Catalog, index.Database, index.Schema, index.Name);
        if ((index.Schema is not null && !string.Equals(index.Schema, table.Schema ?? "main", StringComparison.OrdinalIgnoreCase))
            || (index.Database is not null && !string.Equals(index.Database, table.Database, StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("DuckDB indexes belong to their table's schema and database.");
        // DuckDB infers the index namespace from ON <qualified table> and rejects a qualified CREATE INDEX name.
        return dialect.Renderer.Render(index.Name, "identity");
    }

    public string RenderIdentityPrerequisite(IDialect dialect, IdentityColumnContext column, long seed, long increment, bool dropExisting)
        => string.Join("\r\n", PlanIdentityPrerequisites(dialect, column, seed, increment, dropExisting).Select(command => command.CommandText));

    public IReadOnlyList<SchemaSqlCommand> PlanIdentityPrerequisites(IDialect dialect, IdentityColumnContext column,
        long seed, long increment, bool dropExisting)
    {
        if (increment == 0) throw new ArgumentOutOfRangeException(nameof(increment));
        var name = SequenceName(dialect, column);
        var commands = new List<SchemaSqlCommand>();
        if (dropExisting)
            commands.Add(new($"DROP SEQUENCE IF EXISTS {name};", SchemaSqlOperation.IdentityPrerequisite, name));
        commands.Add(new($"CREATE SEQUENCE {name} START {seed.ToString(CultureInfo.InvariantCulture)} INCREMENT BY {increment.ToString(CultureInfo.InvariantCulture)} MINVALUE -9223372036854775808 MAXVALUE 9223372036854775807 NO CYCLE;",
            SchemaSqlOperation.IdentityPrerequisite, name));
        return commands;
    }

    public string RenderIdentityColumn(IDialect dialect, IdentityColumnContext column, string type)
        => $"{type} DEFAULT nextval({dialect.Renderer.Render(SequenceName(dialect, column), "value")})";

    private static string SequenceName(IDialect dialect, IdentityColumnContext column)
    {
        var name = IdentitySequenceName(column.Table, column.Column);
        return Default.RenderObjectName(dialect, column.Catalog, column.Database, column.Schema, name);
    }

    internal static string IdentitySequenceName(string table, string column)
    {
        // Length-prefixed components avoid collisions between names containing separators or dots.
        var key = string.Concat(new[] { table, column }.Select(part => part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));
        return "duburl_identity_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }
}
