using DubUrl.Querying.Dialects;

namespace DubUrl.Provider.Spark.Sql;

internal sealed class SparkSchemaSqlRenderer : StandardSchemaSqlRenderer, ITableDefinitionSqlRenderer,
    IColumnDefinitionSqlRenderer, ITableDescriptionSqlRenderer
{
    public static new SparkSchemaSqlRenderer Instance { get; } = new();

    public override bool SupportsCommentRendering => true;

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
    {
        RejectQualifier(database, "Database", dialect);
        return ((SparkSqlDialect)dialect).RenderObjectName(name, schema is null ? [] : [schema], catalog);
    }

    public override string RenderColumnType(IDialect dialect, string type, int? length, int precision, int scale, bool nativeType)
    {
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
        if (column.Generation is not null)
            return null;

        // Spark's built-in file tables do not enforce NOT NULL on writes.
        if (column.NotNullable && dialect is not DatabricksSqlDialect)
            throw new NotSupportedException("Spark's built-in table provider cannot enforce NOT NULL. Use a table provider that supports it.");
        var specification = column.Type + (column.NotNullable ? " NOT NULL" : "");
        if (column.Description is not null)
            specification += " COMMENT " + SparkStringFormatter.Quote(column.Description);
        return new(specification, ColumnDefinitionClauses.Nullability | ColumnDefinitionClauses.Comment);
    }

    public string RenderTableDescription(IDialect dialect, SchemaSqlObjectName table, string description)
        => " COMMENT " + SparkStringFormatter.Quote(description);

    public override string RenderColumnComment(IDialect dialect, string description)
        => " COMMENT " + SparkStringFormatter.Quote(description);

    public override string RenderComment(IDialect dialect, SchemaSqlObjectName table, string? column, string description)
        => string.Empty; // Comments are part of the CREATE TABLE statement.

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

        if (table.PrimaryKey is not null || table.UniqueKeys?.Count > 0 || table.Options is not null || table.Indexes?.Count > 0)
            throw new NotSupportedException("Portable Spark SQL table definitions do not support keys, indexes, or physical options.");
        return new("CREATE TABLE", []);
    }

    public override IReadOnlyList<SchemaSqlCommand> PlanCommands(IDialect dialect, IReadOnlyList<SchemaSqlCommand> statements)
    {
        var provider = dialect is DatabricksSqlDialect ? " USING DELTA" : " USING PARQUET";
        return statements.Select(command => command.Operation == SchemaSqlOperation.CreateTable
            ? command with { CommandText = command.CommandText.Insert(ColumnListEnd(command.CommandText) + 1, provider) }
            : command).ToArray();
    }

    private static int ColumnListEnd(string sql)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < sql.Length; i++)
        {
            var character = sql[i];
            if (quote != '\0')
            {
                if (quote == '\'' && character == '\\') i++;
                else if (character == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote) i++;
                    else quote = '\0';
                }
            }
            else if (character is '\'' or '`') quote = character;
            else if (character == '(') depth++;
            else if (character == ')' && --depth == 0) return i;
        }
        throw new InvalidOperationException("The CREATE TABLE command does not contain a complete column list.");
    }

    public string RenderCreateIndex(IDialect dialect, IndexDefinitionContext index)
        => throw new NotSupportedException("Portable Spark SQL index creation is unsupported.");
}
