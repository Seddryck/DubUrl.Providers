using System.Text;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Casters;
using DubUrl.Querying.Dialects.Functions;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Spark.Sql;

[DbTypeMapper<SparkTypeMapper>]
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

    public override DialectCapabilities Capabilities => base.Capabilities with
    {
        SupportsIdentityColumns = true,
        SupportsRegularExpressionChecks = true,
        SupportsStoredGeneratedColumns = true,
    };

    internal bool SupportsInformationalKeys { get; private set; }
    internal bool SupportsUniqueKeys { get; private set; }

    /// <summary>Accepts Databricks informational key semantics for a Unity Catalog target.</summary>
    /// <param name="supportsUnique">Set only when the target supports UNIQUE and foreign keys to UNIQUE targets.</param>
    public DatabricksSqlDialect WithInformationalKeys(bool supportsUnique = false)
    {
        var dialect = (DatabricksSqlDialect)MemberwiseClone();
        dialect.SupportsInformationalKeys = true;
        dialect.SupportsUniqueKeys = supportsUnique;
        return dialect;
    }

    internal bool SupportsTime { get; private set; }

    /// <summary>Enables TIME for a warehouse or runtime that has been verified to support it.</summary>
    public DatabricksSqlDialect WithTimeType()
    {
        var dialect = (DatabricksSqlDialect)MemberwiseClone();
        dialect.SupportsTime = true;
        return dialect;
    }

    internal bool SupportsVariant { get; private set; }

    /// <summary>Enables JSON storage in VARIANT on compute that supports Delta VARIANT columns.</summary>
    public DatabricksSqlDialect WithVariantType()
    {
        var dialect = (DatabricksSqlDialect)MemberwiseClone();
        dialect.SupportsVariant = true;
        return dialect;
    }

    internal bool UsesNameBasedColumnMapping { get; private set; }

    /// <summary>Enables Delta name-based column mapping, including support for spaces in field names.</summary>
    public DatabricksSqlDialect WithNameBasedColumnMapping()
    {
        var dialect = (DatabricksSqlDialect)MemberwiseClone();
        dialect.UsesNameBasedColumnMapping = true;
        return dialect;
    }

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
