using DubUrl.Provider.Oracle.Schema.Generation;
using DubUrl.Querying;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Casters;
using DubUrl.Querying.Dialects.Functions;
using DubUrl.Querying.Dialects.Renderers;
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
