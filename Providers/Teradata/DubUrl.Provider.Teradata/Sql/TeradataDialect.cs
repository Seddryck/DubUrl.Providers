using DubUrl.Provider.Teradata.Schema.Generation;
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

namespace DubUrl.Provider.Teradata.Sql;

[DbTypeMapper<TeradataColumnDefaultTypeMapper>]
[SqlFunctionMapper<AnsiFunctionMapper>]
[Renderer<AnsiRenderer>()]
[ParentLanguage<SqlLanguage>]
public class TeradataDialect : BaseDialect
{
    internal Version? VantageVersion { get; private set; }

    /// <summary>Qualifies typed defaults against the documented Teradata Vantage syntax.</summary>
    public TeradataDialect WithVantageColumnDefaults(Version serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        var dialect = (TeradataDialect)MemberwiseClone();
        dialect.VantageVersion = serverVersion;
        return dialect;
    }

    public override ISchemaSqlRenderer SchemaSqlRenderer => TeradataSchemaSqlRenderer.Default;
    public override DialectCapabilities Capabilities { get; } = new()
    {
        SupportsComments = true,
        NativeTypes = new HashSet<string>(["VARCHAR", "CHAR", "CLOB"], StringComparer.OrdinalIgnoreCase)
    };

    internal TeradataDialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters, IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }
}
