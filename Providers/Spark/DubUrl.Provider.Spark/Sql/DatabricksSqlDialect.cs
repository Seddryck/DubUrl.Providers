using System.Text;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Casters;
using DubUrl.Querying.Dialects.Functions;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Spark.Sql;

[DbTypeMapper<AnsiTypeMapper>]
[SqlFunctionMapper<AnsiFunctionMapper>]
[Renderer<SparkSqlRenderer>]
[ReturnCaster<DateTimeCaster<DateOnly>>]
[ParentLanguage<SqlLanguage>]
public sealed class DatabricksSqlDialect : SparkSqlDialect
{
    private static readonly SparkCatalogNameRules UnityCatalogRules = new(StringComparer.OrdinalIgnoreCase, 1, IsUnityCatalogName);

    internal DatabricksSqlDialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters,
        IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }

    public override SparkSqlDialect WithCatalogRules(string catalog, SparkCatalogNameRules rules, bool isCurrent = false)
        => throw new NotSupportedException("Databricks SQL uses Unity Catalog naming rules.");

    internal override SparkCatalogNameRules GetCatalogRules(string? catalog) => UnityCatalogRules;

    internal override void ValidateCatalogQualifier(string catalog, int namespaceCount)
    {
        if (namespaceCount != 1)
            throw new NotSupportedException("A Databricks catalog qualifier requires exactly one schema.");
        if (!IsUnityCatalogName(catalog))
            throw new ArgumentException($"'{catalog}' is not a valid Unity Catalog name.", nameof(catalog));
    }

    private static bool IsUnityCatalogName(string name)
        => name.EnumerateRunes().Count() <= 255
            && !name.Any(character => character is '.' or ' ' or '/' or <= '\u001F' or '\u007F');
}
