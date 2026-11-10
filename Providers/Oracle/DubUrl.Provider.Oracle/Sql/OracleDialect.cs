using DubUrl.Provider.Oracle.Schema.Generation;
using DubUrl.Querying;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Casters;
using DubUrl.Querying.Dialects.Functions;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Schema;
using DubUrl.Querying.TypeMapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubUrl.Provider.Oracle.Sql;

[DbTypeMapper<AnsiTypeMapper>]
[SqlFunctionMapper<AnsiFunctionMapper>]
[Renderer<AnsiRenderer>()]
[ReturnCaster<DateTimeCaster<DateOnly>>]
[ParentLanguage<SqlLanguage>]
public class OracleDialect : BaseDialect
{
    internal Version? ServerVersion { get; private set; }
    internal Version? CompatibilityVersion { get; private set; }
    private static readonly Version LongIdentifierBaseline = new(12, 2);
    private IReadOnlyList<OracleNamedType> NamedTypes { get; set; } = [];

    /// <summary>Binds a portable struct to a named Oracle object type. Register dependencies first.</summary>
    public OracleDialect WithObjectType(DatabaseObjectName name, StructType shape) => WithNamedType(name, shape, null);

    /// <summary>Binds a portable array to a named Oracle VARRAY with an explicit maximum capacity.</summary>
    public OracleDialect WithVarrayType(DatabaseObjectName name, ArrayType shape, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return WithNamedType(name, shape, capacity);
    }

    /// <summary>Returns Oracle CREATE TYPE commands for provisioning registered types in dependency order.</summary>
    public IReadOnlyList<string> RenderNamedTypeDefinitions() => NamedTypes.Select(type => type.Definition).ToArray();

    internal string? ResolveNamedType(StructuredType shape) => FindNamedType(shape)?.SqlName;

    private OracleNamedType? FindNamedType(StructuredType shape) => NamedTypes.FirstOrDefault(type => type.Shape == shape);

    private OracleDialect WithNamedType(DatabaseObjectName name, StructuredType shape, int? capacity)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(shape);
        var existing = NamedTypes.FirstOrDefault(type => type.Name == name || type.Shape == shape);
        if (existing is not null)
            return existing.Name == name && existing.Shape == shape && existing.Capacity == capacity ? this
                : throw new ArgumentException("Each Oracle named type and shape must have a single binding.");

        var identifierLimit = ServerVersion >= LongIdentifierBaseline && (CompatibilityVersion ?? ServerVersion) >= LongIdentifierBaseline
            ? OracleNamedType.ModernIdentifierLimit : 30;
        var type = OracleNamedType.Create(name, shape, capacity, identifierLimit, FindNamedType);
        var dialect = (OracleDialect)MemberwiseClone();
        dialect.NamedTypes = [.. NamedTypes, type];
        return dialect;
    }

    /// <summary>Sets the server version for schema planning and assumes matching database compatibility.</summary>
    public OracleDialect WithServerVersion(Version serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        var dialect = (OracleDialect)MemberwiseClone();
        dialect.ServerVersion = serverVersion;
        dialect.CompatibilityVersion = null;
        return dialect;
    }

    /// <summary>Sets the server version and the database's configured COMPATIBLE value for schema planning.</summary>
    public OracleDialect WithServerVersion(Version serverVersion, Version compatibilityVersion)
    {
        ArgumentNullException.ThrowIfNull(compatibilityVersion);
        var dialect = WithServerVersion(serverVersion);
        dialect.CompatibilityVersion = compatibilityVersion;
        return dialect;
    }

    public override DialectCapabilities Capabilities { get; } = new()
    {
        SupportsComments = true,
        SupportsViews = true,
        SupportsRegularExpressionChecks = true,
        NativeTypes = new HashSet<string>([
            "CHAR", "VARCHAR", "VARCHAR2", "CHAR BYTE", "CHAR CHAR", "VARCHAR2 BYTE", "VARCHAR2 CHAR",
            "NUMBER", "DATE", "TIMESTAMP", "BINARY_FLOAT", "BINARY_DOUBLE", "RAW", "CLOB", "NCLOB", "BLOB"
        ], StringComparer.OrdinalIgnoreCase),
    };

    public override ISchemaSqlRenderer SchemaSqlRenderer => OracleSchemaSqlRenderer.Default;

    internal OracleDialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters, IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }
}
