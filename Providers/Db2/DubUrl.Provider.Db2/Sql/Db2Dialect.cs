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

namespace DubUrl.Provider.Db2.Sql;

[DbTypeMapper<Db2ColumnDefaultTypeMapper>]
[SqlFunctionMapper<AnsiFunctionMapper>]
[Renderer<AnsiRenderer>()]
[ReturnCaster<DateTimeCaster<DateOnly>>]
[ReturnCaster<TimeSpanCaster<TimeOnly>>]
[ParentLanguage<SqlLanguage>]
public class Db2Dialect : BaseDialect
{
    internal Version? LuwVersion { get; private set; }

    /// <summary>Qualifies typed defaults for Db2 LUW. Db2 for i and z/OS use separate rules.</summary>
    public Db2Dialect WithLuwColumnDefaults(Version serverVersion)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);
        var dialect = (Db2Dialect)MemberwiseClone();
        dialect.LuwVersion = serverVersion;
        return dialect;
    }

    public override ISchemaSqlRenderer SchemaSqlRenderer => Db2ColumnDefaults.Instance;

    public override DialectCapabilities Capabilities { get; } = new() { SupportsComments = true };

    internal Db2Dialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters, IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }
}
