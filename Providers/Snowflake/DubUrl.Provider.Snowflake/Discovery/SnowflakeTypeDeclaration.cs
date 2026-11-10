using System.Data;
using System.Globalization;
using System.Text;
using DubUrl.Provider.Snowflake.Schema.Generation;
using DubUrl.Schema;

namespace DubUrl.Provider.Snowflake.Discovery;

/// <summary>Reads the type declaration returned by DESCRIBE TABLE. Catalog text is never executed.</summary>
internal sealed class SnowflakeTypeDeclaration(string sql)
{
    private static readonly IReadOnlyDictionary<string, DbType> Scalars = SnowflakeStructuredTypes.Scalars
        .ToDictionary(item => item.Value, item => item.Key, StringComparer.OrdinalIgnoreCase);
    private int position;

    internal static string Family(string declaration)
    {
        var parser = new SnowflakeTypeDeclaration(declaration);
        return parser.ReadWord().ToUpperInvariant();
    }

    internal static StructuredType? Parse(string declaration)
    {
        var parser = new SnowflakeTypeDeclaration(declaration);
        var type = parser.ReadType(0);
        parser.SkipSpace();
        return parser.position == declaration.Length ? type : null;
    }

    private StructuredType? ReadType(int depth)
    {
        if (depth >= SnowflakeStructuredTypes.MaxDepth)
            return null;

        var name = ReadWord().ToUpperInvariant();
        return name switch
        {
            "ARRAY" when Take('(') => ReadArray(depth),
            "MAP" when Take('(') => ReadMap(depth),
            "OBJECT" when Take('(') => ReadObject(depth),
            "VECTOR" when depth == 0 && Take('(') => ReadVector(),
            "VARIANT" => new JsonType(),
            _ => ReadScalar(name)
        };
    }

    private ArrayType? ReadArray(int depth)
        => ReadMember(depth) is { } member && Take(')') ? new(member.Type, member.Nullable) : null;

    private MapType? ReadMap(int depth)
        => ReadType(depth + 1) is ScalarType key && SnowflakeStructuredTypes.IsMapKey(key.Type) && Take(',')
            && ReadMember(depth) is { } member && Take(')') ? new(key, member.Type, member.Nullable) : null;

    private StructType? ReadObject(int depth)
    {
        var fields = new List<StructField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var name = ReadFieldName();
            if (name is null || !SnowflakeStructuredTypes.IsFieldName(name) || !names.Add(name)
                || ReadMember(depth) is not { } member)
                return null;
            fields.Add(new(name, member.Type, member.Nullable));
        } while (Take(','));
        return Take(')') ? new(fields) : null;
    }

    private VectorType? ReadVector()
    {
        DbType? element = ReadWord().ToUpperInvariant() switch
        {
            "INT" => DbType.Int32,
            "FLOAT" => DbType.Single,
            _ => null
        };
        return element is { } type && Take(',') && ReadInteger() is > 0 and <= SnowflakeStructuredTypes.MaxVectorDimensions and { } size
            && Take(')') ? new(type, size) : null;
    }

    private ScalarType? ReadScalar(string name)
    {
        if (name == "NUMBER" && Take('('))
        {
            if (ReadInteger() is not { } precision || !Take(',') || ReadInteger() is not { } scale || !Take(')'))
                return null;
            name = FormattableString.Invariant($"NUMBER({precision}, {scale})");
        }
        else if (name == "VARCHAR" && Take('('))
        {
            // DESCRIBE can include the default VARCHAR length even when no length was declared.
            if (ReadInteger() != 16777216 || !Take(')'))
                return null;
        }
        return Scalars.TryGetValue(name, out var type) ? new(type) : null;
    }

    private (StructuredType Type, bool Nullable)? ReadMember(int depth)
    {
        if (ReadType(depth + 1) is not { } type)
            return null;
        var start = position;
        if (ReadWord().Equals("NOT", StringComparison.OrdinalIgnoreCase))
            return ReadWord().Equals("NULL", StringComparison.OrdinalIgnoreCase) ? (type, false) : null;
        position = start;
        return (type, true);
    }

    private string? ReadFieldName()
    {
        if (!Take('"'))
            return ReadWord();

        var name = new StringBuilder();
        while (position < sql.Length)
        {
            var character = sql[position++];
            if (character == '"')
            {
                if (position == sql.Length || sql[position] != '"')
                    return name.ToString();
                position++;
            }
            name.Append(character);
        }
        return null;
    }

    private string ReadWord()
    {
        SkipSpace();
        var start = position;
        while (position < sql.Length && (char.IsAsciiLetterOrDigit(sql[position]) || sql[position] is '_' or '$'))
            position++;
        return sql[start..position];
    }

    private int? ReadInteger()
        => int.TryParse(ReadWord(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private bool Take(char character)
    {
        SkipSpace();
        if (position == sql.Length || sql[position] != character)
            return false;
        position++;
        return true;
    }

    private void SkipSpace()
    {
        while (position < sql.Length && char.IsWhiteSpace(sql[position]))
            position++;
    }
}
