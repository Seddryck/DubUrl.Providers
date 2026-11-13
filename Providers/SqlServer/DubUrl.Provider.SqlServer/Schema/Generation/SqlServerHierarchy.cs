using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DubUrl.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;
using ForeignKeyConstraint = DubUrl.Schema.Constraints.ForeignKeyConstraint;

namespace DubUrl.Provider.SqlServer.Schema.Generation;

internal static class SqlServerHierarchy
{
    internal const string TypeName = "hierarchyid";
    internal const string PathColumn = "node_path";
    internal const string ParentPathColumn = "parent_path";
    internal const string Annotation = "DubUrl.Hierarchy";
    private const string ParentFunction = "SqlServerHierarchyGetParent";
    private static readonly NativeDatabaseType NativeType = new(TypeName, typeof(TSqlDialect));
    private static readonly Regex AncestorExpression = new(
        @"\A\s*\(*\s*\[ (?<path>(?:[^\]]|\]\])+) \]\s*\.\s*(?:GetAncestor|\[GetAncestor\])\s*\(\s*\(?\s*1\s*\)?\s*\)\s*\)*\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace, TimeSpan.FromSeconds(1));

    internal static HierarchyStoragePlan Plan(HierarchyContext context)
    {
        var table = context.Table.Identity;
        ValidateIdentifiers(table, context.Hierarchy.RequireParentExists);
        if (context.Table.Annotations.Keys.Any(IsHierarchyAnnotation))
            throw new NotSupportedException($"Table '{table}' already declares the reserved hierarchy extended property.");

        var hierarchy = context.Hierarchy;
        var path = new Column(PathColumn, DbType.Object, constraints: [new NotNullableConstraint()], nativeType: NativeType);
        var unique = new UniquenessConstraint([path], ConstraintName("UQ", table));
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Annotation] = JsonSerializer.Serialize(new Declaration(1, hierarchy.NodeKey,
                hierarchy.ParentColumnName, hierarchy.RequireParentExists))
        };
        if (annotations[Annotation].Length > 3750)
            throw new NotSupportedException("The SQL Server hierarchy declaration exceeds the extended property size limit.");

        if (!hierarchy.RequireParentExists)
            return new(HierarchyRepresentation.HierarchyId, false, [path], [unique]) { TableAnnotations = annotations };

        var expression = new FunctionExpression(ParentFunction, new ColumnIdentityExpression(PathColumn));
        var parent = new Column(ParentPathColumn, DbType.Object, nativeType: NativeType,
            generation: new GeneratedColumnGeneration(expression));
        var foreignKey = new ForeignKeyConstraint(ConstraintName("FK", table), [ParentPathColumn], table, [PathColumn]);
        return new(HierarchyRepresentation.HierarchyId, true, [path, parent], [unique, foreignKey])
        {
            TableAnnotations = annotations
        };
    }

    internal static bool IsHierarchyAnnotation(string name)
        => string.Equals(name.TrimEnd(' '), Annotation, StringComparison.OrdinalIgnoreCase);

    internal static string? RenderExpression(IDialect dialect, Expression expression)
        => expression is FunctionExpression { Function: ParentFunction, Expressions: [ColumnIdentityExpression path] }
            ? $"{dialect.SchemaSqlRenderer.RenderIdentifier(dialect, path.Name)}.GetAncestor(1)"
            : null;

    internal static MetadataExpression? DecodeParentExpression(string definition)
    {
        var match = AncestorExpression.Match(definition);
        return match.Success
            ? new("function", ParentFunction, Arguments: [new("column", match.Groups["path"].Value.Replace("]]", "]", StringComparison.Ordinal))])
            : null;
    }

    internal static Hierarchy? DecodeDeclaration(string? value)
    {
        if (value is null) return null;
        try
        {
            return JsonSerializer.Deserialize<Declaration>(value) is
                { Version: 1, NodeKey: not null, ParentColumnName: not null, RequireParentExists: { } enforced } declaration
                ? new(declaration.NodeKey, enforced, declaration.ParentColumnName) : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string ConstraintName(string prefix, DatabaseObjectName table)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(table.Key));
        return $"{prefix}_Hierarchy_{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    private static void ValidateIdentifiers(DatabaseObjectName table, bool enforced)
    {
        if (new[] { table.Name, table.Schema, table.Database, table.Catalog }.OfType<string>()
            .Any(name => name.Length > 128 || name.Contains('\0')))
            throw new NotSupportedException("SQL Server hierarchy table identifiers must fit in sysname.");
        if (table.Catalog is not null || (enforced && table.Database is not null))
            throw new NotSupportedException("Generate a SQL Server hierarchy in the target database using a local table name.");
    }

    private sealed record Declaration(int Version, string NodeKey, string ParentColumnName, bool? RequireParentExists);
}