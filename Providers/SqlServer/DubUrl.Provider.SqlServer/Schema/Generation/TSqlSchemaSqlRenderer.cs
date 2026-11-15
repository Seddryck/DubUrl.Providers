using System.Data;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.SqlServer.Schema.Generation;

internal sealed class TSqlSchemaSqlRenderer : StandardSchemaSqlRenderer, IUniqueConstraintSqlRenderer, IColumnDefaultSqlRenderer,
    IStructuredColumnSqlRenderer, IHierarchySqlRenderer, IColumnDefinitionSqlRenderer, ITableAnnotationSqlRenderer
{
    internal static TSqlSchemaSqlRenderer Default { get; } = new();
    internal const int NativeStructuredVersion = 17;
    internal const int MaxVectorDimensions = 1998;
    internal const int MaxExtendedPropertyValueLength = 3750;
    private const string DescriptionProperty = "MS_Description";

    public HierarchyStoragePlan PlanHierarchy(IDialect dialect, HierarchyContext context)
    {
        if (dialect is TSqlDialect { ServerVersion.Major: < 10 })
            throw new NotSupportedException("hierarchyid requires SQL Server 2008 or later.");
        return SqlServerHierarchy.Plan(context);
    }

    public string? RenderHierarchyExpression(IDialect dialect, Expression expression)
        => SqlServerHierarchy.RenderExpression(dialect, expression);

    public ColumnDefinitionPlan? RenderColumnDefinition(IDialect dialect, ColumnDefinitionContext column)
    {
        if (!string.Equals(column.Type, SqlServerHierarchy.TypeName, StringComparison.OrdinalIgnoreCase)
            || column.Generation is not { Identity: false, Expression: { } expression } generation)
            return null;

        var specification = RenderGenerated(dialect, column.Type, expression, generation.Stored);
        if (column.NotNullable)
        {
            if (!generation.Stored)
                throw new NotSupportedException("A non-null SQL Server computed hierarchyid column must be persisted.");
            specification += " NOT NULL";
        }
        return new(specification, ColumnDefinitionClauses.Generation | ColumnDefinitionClauses.Nullability);
    }

    public string? RenderStructuredType(IDialect dialect, StructuredColumnTypeContext context)
    {
        if (context.IsKey || context.Column.Generation is not null)
            return null;

        var nativeTypes = dialect is TSqlDialect { ServerVersion.Major: >= NativeStructuredVersion };
        return context.Type switch
        {
            { Storage: StructuredStorage.JsonText, Shape: JsonType } => "NVARCHAR(MAX)",
            { Storage: StructuredStorage.Json, Shape: JsonType } when nativeTypes => "JSON",
            { Storage: StructuredStorage.Native, Shape: VectorType { ElementType: DbType.Single,
                Dimensions: <= MaxVectorDimensions } vector } when nativeTypes && context.Column.DefaultValue is null
                && !context.Column.Constraints.Any(constraint => constraint is CheckConstraint or MembershipCheckConstraint or RegexCheckConstraint)
                => FormattableString.Invariant($"VECTOR({vector.Dimensions})"),
            _ => null
        };
    }

    public ColumnDefaultPlan? PlanColumnDefault(IDialect dialect, ColumnDefaultContext context)
    {
        if (context.Column.DefaultExpression is not { FractionalSecondsPrecision: null or 7 } expression)
            return null;
        var type = context.SqlType.ToUpperInvariant();
        var unicode = type.StartsWith("NVARCHAR(", StringComparison.Ordinal) || type.StartsWith("NCHAR(", StringComparison.Ordinal);
        return expression.Kind switch
        {
            ColumnDefaultKind.CurrentUser when unicode => new("CURRENT_USER", 128, false),
            ColumnDefaultKind.SessionUser when unicode => new("ORIGINAL_LOGIN()", 128, false),
            ColumnDefaultKind.CurrentSchema when unicode => new("SCHEMA_NAME()", 128, false),
            ColumnDefaultKind.CurrentDatabase when unicode => new("DB_NAME()", 128, false),
            ColumnDefaultKind.NewUuid when type == "UNIQUEIDENTIFIER" && expression.Representation == UuidRepresentation.Native
                => new("NEWID()"),
            ColumnDefaultKind.NextSequenceValue when expression.Sequence is { Schema: not null, Catalog: null } sequence
                && dialect is TSqlDialect { ServerVersion.Major: >= 11 }
                && context.Table.Catalog is null && (sequence.Database is null || sequence.Database == context.Table.Database)
                && IsSequenceType(type)
                => new($"(NEXT VALUE FOR {RenderSequenceName(dialect, sequence)})"),
            ColumnDefaultKind.CurrentTimestamp when type == "DATETIME2" => new("SYSDATETIME()"),
            ColumnDefaultKind.CurrentTimestamp when type == "DATETIMEOFFSET" => new("SYSDATETIMEOFFSET()"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "DATETIME2" => new("SYSUTCDATETIME()"),
            ColumnDefaultKind.CurrentUtcTimestamp when type == "DATETIMEOFFSET"
                => new("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')"),
            ColumnDefaultKind.CurrentDate when type == "DATE" => new("CONVERT(date, SYSDATETIME())"),
            ColumnDefaultKind.CurrentTime when type == "TIME" => new("CONVERT(time, SYSDATETIME())"),
            _ => null
        };
    }

    private string RenderSequenceName(IDialect dialect, DatabaseObjectName name)
        => string.Join('.', new[] { name.Database, name.Schema, name.Name }.OfType<string>()
            .Select(part => RenderIdentifier(dialect, part)));

    private static bool IsSequenceType(string type)
        => type is "TINYINT" or "SMALLINT" or "INT" or "INTEGER" or "BIGINT" or "DECIMAL" or "NUMERIC"
            || type.StartsWith("DECIMAL(", StringComparison.Ordinal) || type.StartsWith("NUMERIC(", StringComparison.Ordinal);

    public UniqueConstraintPlan? PlanUniqueConstraint(IDialect dialect, UniqueConstraintContext context)
    {
        var key = context.Constraint;
        if (key.NullPolicy == UniqueNullPolicy.NullsNotDistinct
            || key.Columns.All(column => column.Constraints.Get<NotNullableConstraint>() is not null))
            return new NativeUniqueConstraintPlan(key.NullPolicy, true);

        if (key.NullPolicy != UniqueNullPolicy.NullsDistinct
            || key.Columns.Any(column => column.Generation is GeneratedColumnGeneration))
            return null;

        var table = context.Table.Identity;
        var identity = new DatabaseObjectName(context.SuggestedName, table.Schema, table.Database, table.Catalog);
        var predicate = string.Join(" AND ", key.Columns.Select(column => $"{RenderIdentifier(dialect, column.Name)} IS NOT NULL"));
        return new UniqueIndexPlan(key.NullPolicy, false, [new(identity, predicate)]);
    }

    public override IReadOnlyList<SchemaSqlCommand> PlanCommands(IDialect dialect, IReadOnlyList<SchemaSqlCommand> statements)
    {
        var commands = new List<SchemaSqlCommand>();
        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            if (statement.Operation != SchemaSqlOperation.Comment)
            {
                // Every view starts its own command/batch. Other statement scopes are never split.
                commands.Add(statement);
                continue;
            }
            var comments = new List<string> { statement.CommandText };
            while (i + 1 < statements.Count && statements[i + 1].Operation == SchemaSqlOperation.Comment)
                comments.Add(statements[++i].CommandText);
            commands.Add(new(string.Join("\r\n", comments), SchemaSqlOperation.Comment, "comments"));
        }
        return commands;
    }

    public override bool SupportsCommentRendering => true;

    public override string RenderColumnType(IDialect dialect, string type, int? length, int precision, int scale, bool nativeType)
    {
        if (type is "NVARCHAR" or "VARCHAR" or "NCHAR" or "CHAR")
        {
            if (length is null)
                throw new ArgumentException($"SQL Server {type} requires an explicit length; use VarLengthColumn.Unbounded for VARCHAR(MAX)/NVARCHAR(MAX).");
            if (length == -1)
                return type is "VARCHAR" or "NVARCHAR" ? $"{type}(MAX)"
                    : throw new NotSupportedException($"SQL Server {type} cannot be unbounded.");
        }
        return base.RenderColumnType(dialect, type, length, precision, scale, nativeType);
    }

    public override void ValidateKeyColumn(IDialect dialect, string type, string table, string column)
    {
        if (type.EndsWith("(MAX)", StringComparison.OrdinalIgnoreCase) || type is "TEXT" or "NTEXT" or "IMAGE")
            throw new ArgumentException($"SQL Server key column '{table}.{column}' uses {type}; supply an explicit bounded key type and length.");
    }

    public override string RenderIdentity(IDialect dialect, string type, long seed, long increment)
        => $"{type} IDENTITY({seed}, {increment})";

    public override string RenderGenerated(IDialect dialect, string type, string expression, bool stored)
        => $"AS ({expression}){(stored ? " PERSISTED" : string.Empty)}";

    public override string RenderIdentifier(IDialect dialect, string name)
        => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    public override string RenderCreateIndexName(IDialect dialect, SchemaSqlObjectName index, SchemaSqlObjectName table)
    {
        if ((index.Catalog is not null && index.Catalog != table.Catalog)
            || (index.Database is not null && index.Database != table.Database)
            || (index.Schema is not null && index.Schema != table.Schema))
            throw new NotSupportedException("SQL Server indexes belong to their table's namespace.");
        return RenderIdentifier(dialect, index.Name);
    }

    public override string RenderObjectName(IDialect dialect, string? catalog, string? database, string? schema, string name)
        => QuoteIdentifierParts(dialect, catalog, database, schema, name);

    public override string RenderComment(IDialect dialect, SchemaSqlObjectName table, string? column, string description)
    {
        if (description.Length > MaxExtendedPropertyValueLength)
            throw new ArgumentException("SQL Server extended-property descriptions cannot exceed 7,500 bytes (3,750 UTF-16 code units).", nameof(description));
        return RenderExtendedProperty(table, DescriptionProperty, description, column);
    }

    public IReadOnlyList<SchemaSqlCommand> PlanTableAnnotations(IDialect dialect, DatabaseObjectName table,
        IReadOnlyDictionary<string, string> annotations)
    {
        var target = new SchemaSqlObjectName(table.Name, table.Schema, table.Database, table.Catalog);
        var commands = new List<SchemaSqlCommand>(annotations.Count);
        foreach (var (key, value) in annotations.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!IsAnnotationName(key))
                throw new NotSupportedException($"SQL Server extended property '{key}' cannot be used as a table annotation.");
            if (value.Length > MaxExtendedPropertyValueLength)
                throw new NotSupportedException($"SQL Server annotation '{key}' exceeds 7,500 bytes (3,750 UTF-16 code units).");
            if (SqlServerHierarchy.IsHierarchyAnnotation(key)
                && (key != SqlServerHierarchy.Annotation || SqlServerHierarchy.DecodeDeclaration(value) is null))
                throw new NotSupportedException("The reserved SQL Server hierarchy annotation requires a valid hierarchy declaration.");

            commands.Add(new(RenderExtendedProperty(target, key, value, column: null), SchemaSqlOperation.Comment, table.ToString()));
        }
        return commands;
    }

    internal static bool IsAnnotationName(string key)
        => key.Length is > 0 and <= 128 && !key.Contains('\0')
            && !string.Equals(key.TrimEnd(' '), DescriptionProperty, StringComparison.OrdinalIgnoreCase);

    private static string RenderExtendedProperty(SchemaSqlObjectName table, string key, string value, string? column)
    {
        if (table.Catalog is not null && table.Database is null)
            throw new NotSupportedException("SQL Server extended properties qualified by a catalog also require a database name.");
        // A separate batch lets each property resolve an omitted schema without redeclaring variables.
        // Running sp_executesql in the target database also resolves its default schema in the right context.
        var scope = table.Catalog is null && table.Database is null ? string.Empty
            : string.Join('.', new[] { table.Catalog, table.Database }.Where(name => name is not null)
                .Select(name => $"[{name!.Replace("]", "]]")}]")) + ".";
        var statement = "DECLARE @schema sysname = COALESCE(@schemaName, SCHEMA_NAME()); "
            + "EXEC sys.sp_addextendedproperty @name = @propertyName, @value = @propertyValue, "
            + "@level0type = N'SCHEMA', @level0name = @schema, @level1type = N'TABLE', @level1name = @tableName"
            + (column is null ? ";" : ", @level2type = N'COLUMN', @level2name = @columnName;");
        var parameters = "@schemaName sysname, @tableName sysname, @propertyName sysname, @propertyValue nvarchar(3750)"
            + (column is null ? string.Empty : ", @columnName sysname");
        return $"EXEC {scope}sys.sp_executesql {UnicodeLiteral(statement)}, {UnicodeLiteral(parameters)}, "
            + $"@schemaName = {(table.Schema is null ? "NULL" : UnicodeLiteral(table.Schema))}, "
            + $"@tableName = {UnicodeLiteral(table.Name)}, @propertyName = {UnicodeLiteral(key)}, @propertyValue = {UnicodeLiteral(value)}"
            + (column is null ? ";" : $", @columnName = {UnicodeLiteral(column)};");
    }

    private static string UnicodeLiteral(string value) => $"N'{value.Replace("'", "''")}'";
}
