using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.DuckDb.Schema.Generation;

/// <summary>DuckDB uses a dedicated sequence and nextval default for each portable identity column.</summary>
internal sealed class DuckDbSchemaSqlRenderer : StandardSchemaSqlRenderer, ISequenceIdentitySqlRenderer, IUniqueConstraintSqlRenderer
{
    internal static DuckDbSchemaSqlRenderer Default { get; } = new();

    public UniqueConstraintPlan? PlanUniqueConstraint(IDialect dialect, UniqueConstraintContext context)
    {
        var key = context.Constraint;
        if (key.NullPolicy != UniqueNullPolicy.NullsDistinct
            && key.Columns.Any(column => column.Constraints.Get<NotNullableConstraint>() is null))
            return null;

        // DuckDB generates native constraint names. A separate index preserves a custom name,
        // but DuckDB only accepts a native UNIQUE constraint as a foreign-key target.
        var table = context.Table.Identity;
        var generatedName = $"{table.Name}_{string.Join('_', key.Columns.Select(column => LowerAscii(column.Name)))}_key";
        if (key.Name is null || key.Name == generatedName)
            return new NativeUniqueConstraintPlan(key.NullPolicy, true);

        return new UniqueIndexPlan(key.NullPolicy, false,
            [new(new DatabaseObjectName(key.Name, table.Schema, table.Database, table.Catalog))]);
    }

    private static string LowerAscii(string name)
        => new(name.Select(character => character is >= 'A' and <= 'Z' ? char.ToLowerInvariant(character) : character).ToArray());

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
        // Length-prefixed components avoid collisions between names containing separators or dots.
        var components = new[] { column.Table, column.Column };
        var key = string.Concat(components.Select(part => part is null ? "-1:" : part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part));
        var name = "duburl_identity_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Default.RenderObjectName(dialect, column.Catalog, column.Database, column.Schema, name);
    }
}
