using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class SparkSchemaSqlRenderer : StandardSchemaSqlRenderer, ITableDefinitionSqlRenderer,
    IColumnDefinitionSqlRenderer, ITableDescriptionSqlRenderer, IColumnDefaultSqlRenderer, ISchemaCommandSqlRenderer,
    IStructuredColumnSqlRenderer
{
    public static new SparkSchemaSqlRenderer Instance { get; } = new();

    public override bool SupportsCommentRendering => true;

    public string RenderStructuredType(IDialect dialect, StructuredColumnTypeContext context)
        => SparkStructuredTypes.Render((SparkSqlDialect)dialect, context);

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(database, "Database", dialect);
        return ((SparkSqlDialect)dialect).RenderObjectName(name, schema is null ? [] : [schema], catalog);
    }

    public override string RenderColumnType(IDialect dialect, string type, int? length, int precision, int scale, bool nativeType)
    {
        if (type == "TIME" && dialect is DatabricksSqlDialect { SupportsTime: true } && length is null && precision == 0 && scale == 0)
            return type;
        if (type == "DECIMAL" && length is null && precision is >= 1 and <= 38 && scale >= 0 && scale <= precision)
            return FormattableString.Invariant($"DECIMAL({precision}, {scale})");
        if (type is "CHAR" or "VARCHAR" && length is > 0 && precision == 0 && scale == 0)
            return FormattableString.Invariant($"{type}({length})");
        if (type == "VARCHAR" && length is null or -1 && precision == 0 && scale == 0)
            return "STRING";
        if (length is null && precision == 0 && scale == 0 && type is
            "BOOLEAN" or "TINYINT" or "SMALLINT" or "INT" or "BIGINT" or "FLOAT" or "DOUBLE"
            or "DECIMAL" or "STRING" or "BINARY" or "DATE" or "TIMESTAMP" or "TIMESTAMP_NTZ" or "TIMESTAMP_LTZ")
            return type;
        throw new NotSupportedException($"Type '{type}' or its length, precision, or scale is unsupported by '{dialect.GetType().Name}'.");
    }

    public ColumnDefinitionPlan? RenderColumnDefinition(IDialect dialect, ColumnDefinitionContext column)
    {
        // Spark's built-in file tables do not enforce NOT NULL on writes.
        if (column.NotNullable && dialect is not DatabricksSqlDialect)
            throw new NotSupportedException("Spark's built-in table provider cannot enforce NOT NULL. Use a table provider that supports it.");
        if (column.DefaultExpression is not null and not "NULL" && dialect is not DatabricksSqlDialect
            && (column.Type.StartsWith("VARCHAR(", StringComparison.Ordinal) || column.Type.StartsWith("CHAR(", StringComparison.Ordinal)))
            throw new NotSupportedException("Spark 3.5 cannot apply a non-NULL default to CHAR or VARCHAR. Use an unbounded string column.");

        var jsonText = column.Type == SparkStructuredTypes.JsonTextSql;
        var description = column.Description;
        if (jsonText)
            description = SparkStructuredTypes.JsonTextCommentPrefix + description;
        else if (column.Type == "STRING" && description is not null && SparkStructuredTypes.IsTaggedComment(description))
            description = SparkStructuredTypes.LiteralCommentPrefix + description;
        var specification = column.Generation switch
        {
            null => jsonText ? "STRING" : column.Type,
            { Identity: true } identity => RenderIdentity(dialect, column.Type, identity.Seed, identity.Increment),
            { Expression: not null } generation => RenderGenerated(dialect, column.Type, generation.Expression, generation.Stored),
            _ => throw new NotSupportedException("A generated column requires an expression."),
        };
        var primary = column.PrimaryKeyColumns.Contains(column.Column, StringComparer.OrdinalIgnoreCase);
        if (primary && column.Nullable)
            throw new NotSupportedException("Databricks primary key columns cannot be nullable.");
        if (column.NotNullable || primary)
            specification += " NOT NULL";
        var clauses = ColumnDefinitionClauses.Nullability | ColumnDefinitionClauses.Comment | ColumnDefinitionClauses.Generation;
        const string timestampLiteralPrefix = "TIMESTAMP ";
        if (column.Type == "TIMESTAMP_NTZ"
            && column.DefaultExpression is { } defaultSql
            && defaultSql.StartsWith(timestampLiteralPrefix + "'", StringComparison.Ordinal))
        {
            specification += " DEFAULT TIMESTAMP_NTZ " + defaultSql[timestampLiteralPrefix.Length..];
            clauses |= ColumnDefinitionClauses.Default;
        }
        if (description is not null)
            specification += " COMMENT " + SparkStringFormatter.Quote(description);
        return new(specification, clauses);
    }

    public override string RenderIdentity(IDialect dialect, string type, long seed, long increment)
    {
        if (dialect is not DatabricksSqlDialect)
            throw new NotSupportedException("Identity columns require Databricks Delta.");
        if (type != "BIGINT")
            throw new NotSupportedException("Databricks Delta identity columns require BIGINT.");
        ArgumentOutOfRangeException.ThrowIfZero(increment);
        return FormattableString.Invariant($"BIGINT GENERATED BY DEFAULT AS IDENTITY (START WITH {seed} INCREMENT BY {increment})");
    }

    public override string RenderGenerated(IDialect dialect, string type, string expression, bool stored)
        => dialect is DatabricksSqlDialect && stored ? $"{type} GENERATED ALWAYS AS ({expression})"
            : throw new NotSupportedException("Only stored generated columns on Databricks Delta are supported.");
    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
        => SparkColumnDefaults.Plan(dialect, context);

    public string RenderTableDescription(IDialect dialect, SchemaSqlObjectName table, string description)
        => " COMMENT " + SparkStringFormatter.Quote(description);

    public override string RenderColumnComment(IDialect dialect, string description)
        => " COMMENT " + SparkStringFormatter.Quote(description);

    public override string RenderComment(IDialect dialect, SchemaSqlObjectName table, string? column, string description)
        => string.Empty; // Comments are part of the CREATE TABLE statement.

    public string RenderTableOptions(IDialect dialect, bool hasDefaults)
    {
        if (dialect is not DatabricksSqlDialect delta)
            return " USING PARQUET";

        var properties = new List<string>();
        if (hasDefaults)
            properties.Add("'delta.feature.allowColumnDefaults' = 'supported'");
        if (delta.UsesNameBasedColumnMapping)
            properties.Add("'delta.columnMapping.mode' = 'name'");
        return properties.Count == 0 ? " USING DELTA" : $" USING DELTA TBLPROPERTIES ({string.Join(", ", properties)})";
    }

    public IReadOnlyList<SchemaSqlCommand> PlanTableChecks(IDialect dialect, DatabaseObjectName table,
        IReadOnlyList<SchemaCheckDefinition> checks)
    {
        if (checks.Count == 0)
            return [];
        if (dialect is not DatabricksSqlDialect)
            throw new NotSupportedException("Spark's built-in table provider does not enforce CHECK constraints. Use Databricks Delta.");
        var name = RenderObjectName(dialect, table.Catalog, table.Database, table.Schema, table.Name);
        if (checks.Any(check => check.Name?.StartsWith(DeltaVectorSql.ConstraintPrefix, StringComparison.OrdinalIgnoreCase) == true))
            throw new NotSupportedException($"Check names beginning with '{DeltaVectorSql.ConstraintPrefix}' are reserved for Delta vectors.");
        return SparkCheckCommands.Plan(dialect, table, name, checks);
    }

    public TableDefinitionPlan RenderTableDefinition(IDialect dialect, TableDefinitionContext table)
    {
        var rules = ((SparkSqlDialect)dialect).GetCatalogRules(table.Table.Catalog);
        var seen = new HashSet<string>(rules.Comparer);
        foreach (var column in table.Columns)
        {
            _ = RenderIdentifier(dialect, column);
            if (!seen.Add(column))
                throw new ArgumentException($"Column '{column}' collides under the selected catalog's naming rules.", nameof(table));
        }

        if (table.Options is not null || table.Indexes?.Count > 0)
            throw new NotSupportedException("Spark SQL table definitions do not support indexes or physical options.");
        if (table.PrimaryKey is null && table.UniqueKeys?.Count is not > 0)
            return new("CREATE TABLE", []);

        var databricks = RequireInformationalKeys(dialect, table.Table.Catalog);
        if (table.UniqueKeys?.Count > 0 && !databricks.SupportsUniqueKeys)
            throw new NotSupportedException("UNIQUE requires a Databricks target that supports informational UNIQUE constraints.");
        var keys = new List<string>();
        if (table.PrimaryKey is { } primary)
            keys.Add(RenderKey(dialect, primary, "PRIMARY KEY"));
        foreach (var key in table.UniqueKeys ?? [])
            keys.Add(RenderKey(dialect, key, "UNIQUE"));
        return new("CREATE TABLE", keys);
    }

    public override bool RequiresForeignKeyDependencyOrder => true;

    public override string RenderForeignKeyTarget(IDialect dialect, SchemaSqlObjectName table, SchemaSqlObjectName target)
    {
        _ = RequireInformationalKeys(dialect, table.Catalog);
        _ = RequireInformationalKeys(dialect, target.Catalog);
        return base.RenderForeignKeyTarget(dialect, table, target);
    }

    private static DatabricksSqlDialect RequireInformationalKeys(IDialect dialect, string? catalog)
    {
        if (dialect is not DatabricksSqlDialect { SupportsInformationalKeys: true } databricks)
            throw new NotSupportedException("Keys on Databricks are informational. Select WithInformationalKeys() on the Databricks dialect to accept this behavior.");
        if (string.Equals(catalog, "hive_metastore", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Informational keys require Unity Catalog.");
        return databricks;
    }

    private string RenderKey(IDialect dialect, SchemaKeyDefinition key, string kind)
    {
        var name = key.Name is null ? "" : $"CONSTRAINT {RenderIdentifier(dialect, key.Name)} ";
        var columns = string.Join(", ", key.Columns.Select(column => RenderIdentifier(dialect, column)));
        return $"{name}{kind} ({columns}) NOT ENFORCED NORELY";
    }

    public override IReadOnlyList<SchemaSqlCommand> PlanCommands(IDialect dialect, IReadOnlyList<SchemaSqlCommand> statements)
        => statements.SelectMany(statement => DeltaVectorSql.Plan(AddForeignKeyOptions(statement))).ToArray();

    private static SchemaSqlCommand AddForeignKeyOptions(SchemaSqlCommand command)
        => command.Operation == SchemaSqlOperation.ForeignKey && command.CommandText.TrimStart().StartsWith("ALTER TABLE ", StringComparison.Ordinal)
            ? command with { CommandText = command.CommandText.TrimEnd().TrimEnd(';') + " NOT ENFORCED NORELY;" }
            : command;

    public IReadOnlyList<SchemaSqlCommand> PlanForeignKeyPrerequisites(IDialect dialect, ForeignKeyConstraint key,
        TableDefinitionContext? createdTarget)
    {
        var databricks = RequireInformationalKeys(dialect, key.TargetTable.Catalog);
        return DatabricksForeignKeys.Plan(databricks, key, createdTarget);
    }

    public override string RenderRegex(IDialect dialect, string expression, string pattern)
        => dialect is DatabricksSqlDialect ? $"({expression} RLIKE {pattern})"
            : throw new NotSupportedException("Regular-expression checks require Databricks Delta.");

    public string RenderCreateIndex(IDialect dialect, IndexDefinitionContext index)
        => throw new NotSupportedException("Portable Spark SQL index creation is unsupported.");
}
