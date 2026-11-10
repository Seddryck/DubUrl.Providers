using System.Data;
using DubUrl.Provider.Access.Sql.Rendering;
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

namespace DubUrl.Provider.Access.Sql;

[DbTypeMapper<MsAccessTypeMapper>]
[SqlFunctionMapper<AnsiFunctionMapper>]
[Renderer<MsAccessRenderer>()]
[ParentLanguage<SqlLanguage>]
public class MsAccessDialect : BaseDialect
{
    internal bool UsesAceOleDb { get; private set; }

    /// <summary>Qualifies ACE OLE DB CREATE TABLE defaults. The default Access dialect also serves ODBC.</summary>
    public MsAccessDialect WithAceOleDbDefaults()
    {
        var dialect = (MsAccessDialect)MemberwiseClone();
        dialect.UsesAceOleDb = true;
        return dialect;
    }

    public override ISchemaSqlRenderer SchemaSqlRenderer => AccessColumnDefaults.Instance;
    public override DialectCapabilities Capabilities { get; } = new()
    {
        NativeTypes = new HashSet<string>(["GUID", "DATETIME"], StringComparer.OrdinalIgnoreCase)
    };

    internal MsAccessDialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters, IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }
}

internal sealed class MsAccessTypeMapper : AnsiTypeMapper
{
    public static new MsAccessTypeMapper Instance { get; } = new();

    private MsAccessTypeMapper()
    {
        AddOrReplace(DbType.DateTime2, "DATETIME");
        AddOrReplace(DbType.Time, "DATETIME");
    }
}
