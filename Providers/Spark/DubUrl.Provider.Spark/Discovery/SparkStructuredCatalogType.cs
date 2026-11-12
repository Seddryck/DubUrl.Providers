using System.Text.Json;
using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;

namespace DubUrl.Provider.Spark.Discovery;

internal static class SparkStructuredCatalogType
{
    internal static bool IsCollection(string name) => name.ToUpperInvariant() is "ARRAY" or "MAP" or "STRUCT";

    internal static bool IsStructured(string? sql)
    {
        if (sql is null) return false;
        var end = sql.AsSpan().IndexOfAny('<', ' ', '\t');
        var name = end < 0 ? sql : sql[..end];
        return IsCollection(name) || name.Equals("VARIANT", StringComparison.OrdinalIgnoreCase);
    }

    internal static StructuredColumnMetadata? ReadSpark(CatalogRow row)
    {
        var sql = row.Text("type_name");
        if (!IsStructured(sql)) return null;
        if (row.Bool("parquet") != true || sql!.Equals("VARIANT", StringComparison.OrdinalIgnoreCase))
            return new(null, MetadataCoverage.Unrepresentable);

        var ddlType = row.Text("column_definition") is { } definition ? SparkCatalogDefinition.StructuredSql(definition) : null;
        return ReadSql(ddlType ?? sql!, ddlType is not null);
    }

    internal static StructuredColumnMetadata? ReadDelta(CatalogRow row)
    {
        var sql = row.Text("type_name");
        if (!IsStructured(sql)) return null;
        if (sql!.Equals("VARIANT", StringComparison.OrdinalIgnoreCase))
            return new(new(new JsonType(), StructuredStorage.Json), MetadataCoverage.Available);

        if (row.Text("structured_json") is { } json)
        {
            using var document = JsonDocument.Parse(json);
            var reader = new DeltaTypeReader();
            var type = reader.Read(document.RootElement, requiredPath: row.Bool("nullable") == false, depth: 0);
            if (reader.Coverage == MetadataCoverage.Available && type is ArrayType { Element: ScalarType { Type: DbType.Single } }
                && row.Int("vector_dimensions") is { } dimensions)
                type = new VectorType(DbType.Single, dimensions);
            return Native(type, type is null ? MetadataCoverage.Unrepresentable : reader.Coverage);
        }
        return ReadSql(sql, hasDdl: false);
    }

    internal static (Dictionary<string, JsonElement> Columns, JsonElement TableProperties)? DeltaDescription(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var columns = Property(document.RootElement, "columns");
            if (columns.ValueKind != JsonValueKind.Array) return null;

            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns.EnumerateArray())
            {
                var name = Text(column, "name");
                if (name is null || !result.TryAdd(name, column.Clone())) return null;
            }
            var properties = Text(document.RootElement, "provider")?.Equals("delta", StringComparison.OrdinalIgnoreCase) == true
                && Property(document.RootElement, "table_properties") is { ValueKind: JsonValueKind.Object } tableProperties
                ? tableProperties.Clone() : default;
            return (result, properties);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonElement Property(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    internal static bool? Flag(JsonElement item, string name) => Property(item, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    internal static string? Text(JsonElement item, string name)
        => Property(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static StructuredColumnMetadata ReadSql(string sql, bool hasDdl)
    {
        var reader = new SqlTypeReader(sql, hasDdl);
        var type = reader.Read();
        var coverage = hasDdl ? reader.Coverage : MetadataCoverage.Inaccessible;
        return Native(type, coverage);
    }

    private static StructuredColumnMetadata Native(StructuredType? type, MetadataCoverage coverage)
        => new(type is null ? null : new(type, StructuredStorage.Native), coverage);

    private sealed class SqlTypeReader(string sql, bool hasDdl)
    {
        private readonly List<SparkSqlSyntax.Token> tokens = SparkSqlSyntax.Tokenize(sql);
        private int position;
        internal MetadataCoverage Coverage { get; private set; } = MetadataCoverage.Available;

        internal StructuredType? Read()
        {
            var type = ReadType(0);
            if (type is null || position != tokens.Count)
            {
                Coverage = MetadataCoverage.Unrepresentable;
                return null;
            }
            return type;
        }

        private StructuredType? ReadType(int depth)
        {
            if (depth >= SparkStructuredTypes.MaxDepth || position >= tokens.Count) return null;

            var name = tokens[position++].Text;
            if (!IsCollection(name))
                return SparkStructuredTypes.ReadScalar(name);
            if (!Take("<")) return null;

            var nullable = hasDdl ? true : (bool?)null;
            switch (name.ToUpperInvariant())
            {
                case "ARRAY":
                    return ReadType(depth + 1) is { } element && Take(">") ? new ArrayType(element, nullable) : null;
                case "MAP":
                    return ReadType(depth + 1) is ScalarType key && Take(",") && ReadType(depth + 1) is { } value && Take(">")
                        ? new MapType(key, value, nullable) : null;
                default:
                    return ReadStruct(depth, nullable);
            }
        }

        private StructType? ReadStruct(int depth, bool? nullable)
        {
            var fields = new List<StructField>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                if (position >= tokens.Count || ReadFieldName(tokens[position++].Text) is not { } name || !names.Add(name)
                    || !Take(":") || ReadType(depth + 1) is not { } fieldType)
                    return null;

                var fieldNullable = nullable;
                if (Take("NOT"))
                {
                    if (!Take("NULL")) return null;
                    fieldNullable = false;
                    Coverage = MetadataCoverage.Unrepresentable;
                }
                if (Take("COMMENT"))
                {
                    if (position >= tokens.Count || !SparkCatalogDefinition.TryString(tokens[position++].Text, out _)) return null;
                    Coverage = MetadataCoverage.Unrepresentable;
                }
                fields.Add(new(name, fieldType, fieldNullable));
            } while (Take(","));
            return Take(">") ? new(fields) : null;
        }

        private static string? ReadFieldName(string token)
        {
            if (token.Length >= 2 && token[0] == '`' && token[^1] == '`')
                token = token[1..^1].Replace("``", "`", StringComparison.Ordinal);
            else if (!token.All(character => char.IsLetterOrDigit(character) || character == '_'))
                return null;
            return ValidName(token) ? token : null;
        }

        private bool Take(string expected)
        {
            if (position == tokens.Count || !tokens[position].Is(expected)) return false;
            position++;
            return true;
        }
    }

    private sealed class DeltaTypeReader
    {
        internal MetadataCoverage Coverage { get; private set; } = MetadataCoverage.Available;

        internal StructuredType? Read(JsonElement type, bool requiredPath, int depth)
        {
            if (depth >= SparkStructuredTypes.MaxDepth || Text(type, "name") is not { } name)
                return null;

            switch (name.ToUpperInvariant())
            {
                case "ARRAY":
                    var elementNullable = Flag(type, "element_nullable");
                    CheckNullability(elementNullable, requiredAllowed: false);
                    return Read(Property(type, "element_type"), false, depth + 1) is { } element
                        ? new ArrayType(element, elementNullable) : null;
                case "MAP":
                    var valueNullable = Flag(type, "value_nullable") ?? Flag(type, "element_nullable");
                    CheckNullability(valueNullable, requiredAllowed: false);
                    return Read(Property(type, "key_type"), false, depth + 1) is ScalarType key
                        && Read(Property(type, "value_type"), false, depth + 1) is { } value
                        ? new MapType(key, value, valueNullable) : null;
                case "STRUCT":
                    return ReadStruct(Property(type, "fields"), requiredPath, depth);
                default:
                    var collation = Text(type, "collation");
                    return collation is null || collation.Equals("UTF8_BINARY", StringComparison.OrdinalIgnoreCase)
                        ? SparkStructuredTypes.ReadScalar(name) : null;
            }
        }

        private StructType? ReadStruct(JsonElement items, bool requiredPath, int depth)
        {
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;

            var fields = new List<StructField>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (Text(item, "name") is not { } name || !ValidName(name) || !names.Add(name)) return null;
                var nullable = Flag(item, "nullable");
                CheckNullability(nullable, requiredPath);
                if (Text(item, "comment") is { Length: > 0 } || Property(item, "default").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    Coverage = MetadataCoverage.Unrepresentable;
                if (Read(Property(item, "type"), requiredPath && nullable == false, depth + 1) is not { } type)
                    return null;
                fields.Add(new(name, type, nullable));
            }
            return new(fields);
        }

        private void CheckNullability(bool? nullable, bool requiredAllowed)
        {
            if (nullable is null || (nullable == false && !requiredAllowed))
                Coverage = MetadataCoverage.Unrepresentable;
        }
    }

    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && !name.Contains('\0');
}
