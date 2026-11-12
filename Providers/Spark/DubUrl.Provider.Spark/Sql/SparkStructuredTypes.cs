using System.Data;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;

namespace DubUrl.Provider.Spark.Sql;

internal static class SparkStructuredTypes
{
    internal const int MaxDepth = 64;
    internal const string JsonTextSql = "STRING /* DubUrl JSON text */";
    internal const string JsonTextCommentPrefix = "[duburl:json-text:v1]";
    internal const string LiteralCommentPrefix = "[duburl:literal-comment:v1]";

    private static readonly Dictionary<DbType, string> Scalars = new()
    {
        [DbType.Boolean] = "BOOLEAN",
        [DbType.SByte] = "TINYINT",
        [DbType.Int16] = "SMALLINT",
        [DbType.Int32] = "INT",
        [DbType.Int64] = "BIGINT",
        [DbType.Single] = "FLOAT",
        [DbType.Double] = "DOUBLE",
        [DbType.String] = "STRING",
        [DbType.Binary] = "BINARY",
        [DbType.Date] = "DATE",
    };

    private static readonly Dictionary<string, DbType> ScalarNames = Scalars.ToDictionary(
        pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    internal static ScalarType? ReadScalar(string name) => ScalarNames.TryGetValue(name, out var type) ? new(type) : null;

    internal static bool IsTaggedComment(string comment)
        => comment.StartsWith(JsonTextCommentPrefix, StringComparison.Ordinal)
            || comment.StartsWith(LiteralCommentPrefix, StringComparison.Ordinal);

    internal static string Render(SparkSqlDialect dialect, StructuredColumnTypeContext context)
    {
        var delta = dialect as DatabricksSqlDialect;
        if (context.Type.Storage == StructuredStorage.Binary)
            throw new NotSupportedException("Binary storage is unsupported for Spark SQL and Databricks structured columns.");
        if (context.Type.Shape is JsonType { ObjectShape: not null })
            throw new NotSupportedException("Shaped JSON is unsupported for Spark SQL and Databricks structured columns.");
        if (context.IsKey || context.Column.Generation is not null)
            throw new NotSupportedException("Structured columns cannot participate in keys or use column generation.");
        if (delta is null && context.Nullable == false)
            throw new NotSupportedException("Spark Parquet cannot enforce NOT NULL on a structured column.");

        return (context.Type.Storage, context.Type.Shape) switch
        {
            (StructuredStorage.JsonText, JsonType) => JsonTextSql,
            (StructuredStorage.Json, JsonType) when delta is { SupportsVariant: true } => "VARIANT",
            (StructuredStorage.Json, JsonType) when delta is not null =>
                throw new NotSupportedException("Select WithVariantType() for Databricks compute that supports VARIANT."),
            (StructuredStorage.Json, JsonType) =>
                throw new NotSupportedException("Spark 3.5 Parquet has no VARIANT column type. Use JsonText for caller-encoded JSON."),
            (StructuredStorage.Native, ArrayType or MapType or StructType) =>
                RenderNative(context.Type.Shape, delta, dialect.GetCatalogRules(context.Table.Catalog).Comparer,
                    requiredPath: delta is not null && context.Nullable == false, depth: 0),
            (StructuredStorage.Native, VectorType { ElementType: DbType.Single } vector) when delta is not null =>
                DeltaVectorSql.Type(vector.Dimensions),
            (_, VectorType) =>
                throw new NotSupportedException("Fixed-length vectors require enforced length and non-null elements with recoverable catalog evidence."),
            _ => throw new NotSupportedException($"Storage '{context.Type.Storage}' is unsupported for '{context.Type.Shape.GetType().Name}'."),
        };
    }

    private static string RenderNative(StructuredType type, DatabricksSqlDialect? delta, StringComparer names,
        bool requiredPath, int depth)
    {
        if (depth >= MaxDepth)
            throw new NotSupportedException($"Structured nesting must be less than {MaxDepth} levels.");

        string RenderChild(StructuredType child, bool required = false)
            => RenderNative(child, delta, names, required, depth + 1);

        switch (type)
        {
            case ScalarType scalar:
                return Scalars.GetValueOrDefault(scalar.Type)
                    ?? throw new NotSupportedException($"Nested scalar type '{scalar.Type}' has no reversible Spark SQL mapping.");
            case ArrayType array:
                if (array.ElementNullable != true)
                    throw new NotSupportedException("ARRAY requires explicitly nullable elements.");
                return $"ARRAY<{RenderChild(array.Element)}>";
            case MapType map:
                if (map.Key is not ScalarType)
                    throw new NotSupportedException("MAP requires a non-null scalar key.");
                if (map.ValueNullable != true)
                    throw new NotSupportedException("MAP requires explicitly nullable values.");
                return $"MAP<{RenderChild(map.Key)}, {RenderChild(map.Value)}>";
            case StructType structure:
                var fields = new List<string>(structure.Fields.Count);
                var seen = new HashSet<string>(names);
                var formatter = new SparkIdentifierFormatter();
                foreach (var field in structure.Fields)
                {
                    if (!seen.Add(field.Name))
                        throw new NotSupportedException($"Struct field '{field.Name}' collides under the selected catalog's naming rules.");
                    if (field.Nullable is null)
                        throw new NotSupportedException($"Struct field '{field.Name}' requires explicit nullability.");
                    if (field.Nullable == false && !requiredPath)
                        throw new NotSupportedException(delta is null
                            ? $"Spark Parquet cannot enforce NOT NULL on struct field '{field.Name}'."
                            : $"NOT NULL on struct field '{field.Name}' requires a NOT NULL column and enclosing structs, with no ARRAY or MAP ancestor.");
                    if (delta is null && field.Name.IndexOfAny([',', ':', ';']) >= 0)
                        throw new NotSupportedException($"Spark's Hive metastore rejects struct field '{field.Name}' because its name contains a comma, colon, or semicolon.");
                    if (delta is { UsesNameBasedColumnMapping: false } && field.Name.IndexOfAny([' ', ',', ';', '{', '}', '(', ')', '\n', '\t', '=']) >= 0)
                        throw new NotSupportedException($"Struct field '{field.Name}' requires Databricks WithNameBasedColumnMapping().");
                    var fieldType = RenderChild(field.Type, requiredPath && field.Nullable == false);
                    fields.Add($"{formatter.Format(field.Name)}: {fieldType}{(field.Nullable == false ? " NOT NULL" : "")}");
                }
                return $"STRUCT<{string.Join(", ", fields)}>";
            default:
                throw new NotSupportedException($"Nested '{type.GetType().Name}' has no reversible Spark SQL mapping.");
        }
    }
}
