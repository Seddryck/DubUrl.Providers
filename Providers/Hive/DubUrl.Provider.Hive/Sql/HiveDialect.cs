using DubUrl.Provider.Hive.Sql.Rendering;
using DubUrl.Querying;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Casters;
using DubUrl.Querying.Dialects.Functions;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Hive.Sql;

[DbTypeMapper<AnsiTypeMapper>]
[SqlFunctionMapper<AnsiFunctionMapper>]
[Renderer<HiveRenderer>]
[ReturnCaster<Parser<DateOnly>>]
[ReturnCaster<DateTimeCaster<DateOnly>>]
[ParentLanguage<SqlLanguage>]
public sealed class HiveDialect : BaseDialect
{
    public override ISchemaSqlRenderer SchemaSqlRenderer => HiveSchemaSqlRenderer.Instance;
    public override DialectCapabilities Capabilities => base.Capabilities with { SupportsComments = true };

    internal HiveDialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters,
        IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }
}
