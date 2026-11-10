using System.Data;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Sqlite.Schema.Generation;

internal sealed class SqliteSchemaSqlRenderer : StandardSchemaSqlRenderer, IUniqueConstraintSqlRenderer, IColumnDefinitionSqlRenderer, IColumnDefaultSqlRenderer
{
    internal static SqliteSchemaSqlRenderer Default { get; } = new();

    internal const string UuidText = "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || "
        + "substr(hex(randomblob(2)), 2) || '-' || substr('89ab', (random() & 3) + 1, 1) || "
        + "substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))";

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or 0 } expression)
            return null;
        var type = context.SqlType.ToUpperInvariant();
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentTimestamp when type == "TEXT" && context.Column.Type != DbType.DateTimeOffset
                => new("CURRENT_TIMESTAMP"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "TEXT" && context.Column.Type != DbType.DateTimeOffset
                => new("(datetime('now'))"),
            ColumnDefaultKind.CurrentDate when type == "TEXT" => new("CURRENT_DATE"),
            ColumnDefaultKind.CurrentTime when type is "TIME" or "TEXT" => new("CURRENT_TIME"),
            ColumnDefaultKind.NewUuid when expression.Representation == UuidRepresentation.CanonicalText
                && type.StartsWith("TEXT(", StringComparison.Ordinal)
                => new($"({UuidText})", UuidSemantics: new(Version: 4, Randomness: "SQLite built-in pseudo-random generator")),
            _ => null
        };
    }

    public UniqueConstraintPlan? PlanUniqueConstraint(IDialect dialect, UniqueConstraintContext context)
    {
        var key = context.Constraint;
        return key.NullPolicy == UniqueNullPolicy.NullsDistinct
            || key.Columns.All(column => column.Constraints.Get<NotNullableConstraint>() is not null)
            ? new NativeUniqueConstraintPlan(key.NullPolicy, true)
            : null;
    }

    public ColumnDefinitionPlan? RenderColumnDefinition(IDialect dialect, ColumnDefinitionContext column)
    {
        if (column.Generation is not { Identity: true } identity) return null;
        if (identity.Seed != 1 || identity.Increment != 1)
            throw new NotSupportedException("SQLite identity columns require seed 1 and increment 1.");
        if (column.Table is null || column.PrimaryKeyColumns.Count != 1 || column.PrimaryKeyColumns[0] != column.Column)
            throw new NotSupportedException("SQLite identity columns require a single-column primary key on the identity column.");
        if (!string.Equals(column.Type, "INTEGER", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SQLite identity columns require the exact INTEGER SQL type.");
        if (column.Nullable)
            throw new NotSupportedException("SQLite identity primary keys cannot be explicitly nullable.");
        var keyName = column.PrimaryKeyName is null ? string.Empty
            : $"CONSTRAINT {dialect.Renderer.Render(column.PrimaryKeyName, "identity")} ";
        return new($"INTEGER {keyName}PRIMARY KEY AUTOINCREMENT",
            ColumnDefinitionClauses.Generation | ColumnDefinitionClauses.PrimaryKey | ColumnDefinitionClauses.Nullability);
    }

    public override bool RenderForeignKeysInline(IDialect dialect) => true;

    public override string RenderIdentity(IDialect dialect, string type, long seed, long increment)
        => throw new NotSupportedException("SQLite auto-increment requires the table-specific INTEGER PRIMARY KEY AUTOINCREMENT form and cannot represent a provider-neutral identity column.");

    public override string RenderGenerated(IDialect dialect, string type, string expression, bool stored)
        => RenderGeneratedColumn(type, expression, stored);

    public override string RenderCreateIndexName(IDialect dialect, SchemaSqlObjectName index, SchemaSqlObjectName table)
    {
        ValidateSameDatabase(index, table);
        return RenderObjectName(dialect, index.Catalog, index.Database ?? table.Database, index.Schema, index.Name);
    }

    public override string RenderIndexTableName(IDialect dialect, SchemaSqlObjectName table)
    {
        RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        return QuoteNames(table.Name);
    }

    public override string RenderForeignKeyTarget(IDialect dialect, SchemaSqlObjectName table, SchemaSqlObjectName target)
    {
        ValidateSameDatabase(target, table);
        RenderObjectName(dialect, target.Catalog, target.Database, target.Schema, target.Name);
        return QuoteNames(target.Name);
    }

    public override string RenderIdentifier(IDialect dialect, string name) => QuoteNames(name);

    private static string QuoteNames(params string?[] parts)
        => string.Join('.', parts.Where(part => part is not null).Select(part => "\"" + part!.Replace("\"", "\"\"") + "\""));

    private static void ValidateSameDatabase(SchemaSqlObjectName name, SchemaSqlObjectName table)
    {
        if (name.Database is not null && name.Database != (table.Database ?? "main"))
            throw new NotSupportedException("SQLite indexes and foreign keys must belong to the same database as their table.");
    }

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(catalog, "Catalog", dialect);
        RejectQualifier(schema, "Schema", dialect);
        return QuoteNames(database, name);
    }
}
