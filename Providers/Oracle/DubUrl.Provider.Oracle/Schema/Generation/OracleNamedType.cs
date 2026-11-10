using System.Data;
using System.Globalization;
using System.Text;
using DubUrl.Schema;

namespace DubUrl.Provider.Oracle.Schema.Generation;

internal sealed record OracleNamedType(DatabaseObjectName Name, StructuredType Shape, int? Capacity, string SqlName, string Definition, int Depth)
{
    internal const int MaxDepth = 32;
    internal const int ModernIdentifierLimit = 128;

    private static readonly IReadOnlyDictionary<DbType, string> Scalars = new Dictionary<DbType, string>
    {
        [DbType.Int16] = "NUMBER(5, 0)",
        [DbType.Int32] = "NUMBER(10, 0)",
        [DbType.Int64] = "NUMBER(19, 0)",
        [DbType.Single] = "BINARY_FLOAT",
        [DbType.Double] = "BINARY_DOUBLE",
        [DbType.String] = "VARCHAR2(4000 BYTE)",
        [DbType.DateTime] = "DATE"
    };

    private static readonly IReadOnlyDictionary<string, DbType> CatalogScalars =
        Scalars.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    internal static OracleNamedType Create(DatabaseObjectName name, StructuredType shape, int? capacity, int identifierLimit,
        Func<StructuredType, OracleNamedType?> resolve)
    {
        if (name.Catalog is not null || name.Database is not null || name.Schema is null
            || !ValidIdentifier(name.Schema, identifierLimit) || !ValidIdentifier(name.Name, identifierLimit))
            throw new ArgumentException("An Oracle named type requires a schema and valid identifiers.", nameof(name));

        var sqlName = QualifiedName(name.Schema, name.Name);
        var depth = 1;
        string Element(StructuredType item)
        {
            if (item is ScalarType scalar && Scalars.TryGetValue(scalar.Type, out var sql))
                return sql;
            if (resolve(item) is not { } dependency)
                throw new NotSupportedException("The member requires a supported scalar or a registered Oracle named type.");
            depth = Math.Max(depth, dependency.Depth + 1);
            return dependency.SqlName;
        }

        string body;
        if (shape is ArrayType array)
        {
            if (array.ElementNullable != true)
                throw new NotSupportedException("The Oracle VARRAY mapping requires nullable elements.");
            body = FormattableString.Invariant($"VARRAY({capacity}) OF {Element(array.Element)}");
        }
        else if (shape is StructType structure)
        {
            var fields = new List<string>(structure.Fields.Count);
            foreach (var field in structure.Fields)
            {
                if (!ValidIdentifier(field.Name, identifierLimit))
                    throw new ArgumentException("Oracle object attribute names must be valid identifiers.", nameof(shape));
                if (field.Nullable != true)
                    throw new NotSupportedException("Oracle object attributes require nullable fields.");
                fields.Add($"\"{field.Name}\" {Element(field.Type)}");
            }
            body = $"OBJECT ({string.Join(", ", fields)})";
        }
        else
            throw new NotSupportedException("Oracle named types support object and VARRAY shapes.");

        if (depth > MaxDepth)
            throw new NotSupportedException("Oracle named type nesting exceeds the supported catalog depth.");
        return new(name, shape, capacity, sqlName, $"CREATE TYPE {sqlName} AS {body}", depth);
    }

    internal static ScalarType? DecodeScalar(string? name, int? precision, int? scale, int? length, string? characterUnit)
    {
        if (name == "NUMBER" && precision is { } p && scale is { } s)
            name = string.Create(CultureInfo.InvariantCulture, $"NUMBER({p}, {s})");
        else if (name == "VARCHAR2" && length == 4000 && characterUnit == "B")
            name = "VARCHAR2(4000 BYTE)";
        return name is not null && CatalogScalars.TryGetValue(name, out var type) ? new(type) : null;
    }

    internal static string QualifiedName(string schema, string name) => $"\"{schema}\".\"{name}\"";

    internal static bool ValidIdentifier(string name, int limit = ModernIdentifierLimit)
        => !string.IsNullOrWhiteSpace(name) && !name.Contains('\0') && !name.Contains('"') && Encoding.UTF8.GetByteCount(name) <= limit;
}
