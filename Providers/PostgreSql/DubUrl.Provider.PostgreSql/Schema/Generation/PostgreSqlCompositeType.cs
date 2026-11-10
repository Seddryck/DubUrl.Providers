using System.Text;
using DubUrl.Schema;

namespace DubUrl.Provider.PostgreSql.Schema.Generation;

internal sealed record PostgreSqlCompositeType(DatabaseObjectName Name, StructType Shape, string SqlName, string Definition, int Depth)
{
    internal const int MaxDepth = 32;

    internal static PostgreSqlCompositeType Create(DatabaseObjectName name, StructType shape, Func<StructType, PostgreSqlCompositeType?> resolve)
    {
        if (name.Catalog is not null || name.Database is not null || name.Schema is null
            || !ValidIdentifier(name.Schema) || !ValidIdentifier(name.Name))
            throw new ArgumentException("A PostgreSQL composite type requires a schema and valid identifiers.", nameof(name));

        var fields = new List<string>(shape.Fields.Count);
        var depth = 1;
        foreach (var field in shape.Fields)
        {
            if (!ValidIdentifier(field.Name))
                throw new ArgumentException("Composite field names must fit PostgreSQL's identifier limit.", nameof(shape));
            if (field.Nullable != true)
                throw new NotSupportedException("PostgreSQL composite attributes require nullable fields.");
            var dependency = field.Type is StructType nested ? resolve(nested) : null;
            var type = dependency is not null ? dependency.SqlName
                : PostgreSqlStructuredLeaf.Render(field.Type) is { } leaf ? $"pg_catalog.{leaf}" : null;
            if (type is null)
                throw new NotSupportedException("A composite attribute requires a supported leaf or a registered nested composite.");
            depth = Math.Max(depth, (dependency?.Depth ?? 0) + 1);
            fields.Add($"{PostgreSqlSchemaSqlRenderer.QuoteIdentifier(field.Name)} {type}");
        }
        if (depth > MaxDepth)
            throw new NotSupportedException("The composite nesting exceeds the supported catalog depth.");

        var sqlName = QualifiedName(name.Schema, name.Name);
        return new(name, shape, sqlName, $"CREATE TYPE {sqlName} AS ({string.Join(", ", fields)});", depth);
    }

    internal static string QualifiedName(string schema, string name)
        => $"{PostgreSqlSchemaSqlRenderer.QuoteIdentifier(schema)}.{PostgreSqlSchemaSqlRenderer.QuoteIdentifier(name)}";

    internal static bool ValidIdentifier(string name)
        => !string.IsNullOrWhiteSpace(name) && !name.Contains('\0') && Encoding.UTF8.GetByteCount(name) <= 63;
}
