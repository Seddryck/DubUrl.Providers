using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Casters;
using DubUrl.Querying.Dialects.Functions;
using DubUrl.Querying.Dialects.Renderers;
using DubUrl.Querying.TypeMapping;

namespace DubUrl.Provider.Spark.Sql;

/// <summary>Naming rules for tables, namespaces and columns in a configured Spark catalog.</summary>
/// <remarks>The optional validator applies to table and namespace components.</remarks>
public sealed record SparkCatalogNameRules(StringComparer Comparer, int? MaxNamespaceParts = null,
    Func<string, bool>? IsValidObjectComponent = null);

[DbTypeMapper<AnsiTypeMapper>]
[SqlFunctionMapper<AnsiFunctionMapper>]
[Renderer<SparkSqlRenderer>]
[ParentLanguage<SqlLanguage>]
public class SparkSqlDialect : BaseDialect
{
    private const string SessionCatalog = "spark_catalog";
    private static readonly SparkCatalogNameRules SessionCatalogRules = new(StringComparer.OrdinalIgnoreCase, 1,
        name => name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));
    private static readonly SparkCatalogNameRules UnconfiguredCatalogRules = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, SparkCatalogNameRules> catalogRules =
        new Dictionary<string, SparkCatalogNameRules>(StringComparer.OrdinalIgnoreCase);
    private string? currentCatalog;

    public override ISchemaSqlRenderer SchemaSqlRenderer => SparkSchemaSqlRenderer.Instance;
    public override DialectCapabilities Capabilities { get; } = new()
    {
        ObjectNames = ObjectNameParts.Name | ObjectNameParts.Schema | ObjectNameParts.Catalog,
    };

    internal SparkSqlDialect(ILanguage language, string[] aliases, IRenderer renderer, ICaster[] casters,
        IDbTypeMapper dbTypeMapper, ISqlFunctionMapper sqlFunctionMapper)
        : base(language, aliases, renderer, casters, dbTypeMapper, sqlFunctionMapper) { }

    /// <summary>Applies the catalog's rules locally. Marking it current affects validation only.</summary>
    public virtual SparkSqlDialect WithCatalogRules(string catalog, SparkCatalogNameRules rules, bool isCurrent = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalog);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rules.Comparer);
        ArgumentOutOfRangeException.ThrowIfNegative(rules.MaxNamespaceParts ?? 0);

        var dialect = (SparkSqlDialect)MemberwiseClone();
        dialect.catalogRules = new Dictionary<string, SparkCatalogNameRules>(catalogRules, StringComparer.OrdinalIgnoreCase)
        {
            [catalog] = rules,
        };
        if (isCurrent)
            dialect.currentCatalog = catalog;
        return dialect;
    }

    /// <summary>Renders literal namespace components in order. Omitted qualifiers are resolved by Spark.</summary>
    public string RenderObjectName(string name, IReadOnlyList<string> namespaceParts, string? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(namespaceParts);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var rules = GetCatalogRules(catalog);
        if (catalog is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(catalog);
            ValidateCatalogQualifier(catalog, namespaceParts.Count);
        }

        if (namespaceParts.Count > rules.MaxNamespaceParts)
            throw new NotSupportedException($"The selected catalog allows at most {rules.MaxNamespaceParts} namespace components.");

        foreach (var part in namespaceParts)
        {
            ArgumentException.ThrowIfNullOrEmpty(part);
            ValidateObjectComponent(part, rules);
        }
        ValidateObjectComponent(name, rules);

        var components = namespaceParts.Append(name);
        if (catalog is not null)
            components = components.Prepend(catalog);
        return string.Join('.', components.Select(part => Renderer.Render(part, "identity")));
    }

    /// <summary>Checks for collisions among names with the same supplied qualification in one catalog.</summary>
    public void ValidateObjectNames(IEnumerable<SchemaSqlObjectName> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var catalog in names.GroupBy(name => name.Catalog ?? currentCatalog ?? SessionCatalog,
                     StringComparer.OrdinalIgnoreCase))
        {
            var seen = new HashSet<string>(GetCatalogRules(catalog.Key).Comparer);
            foreach (var name in catalog)
            {
                var rendered = SchemaSqlRenderer.RenderObjectName(this, name.Catalog, name.Database, name.Schema, name.Name);
                if (!seen.Add(rendered))
                    throw new ArgumentException($"Object name {rendered} collides in the selected catalog.", nameof(names));
            }
        }
    }

    internal virtual SparkCatalogNameRules GetCatalogRules(string? catalog)
    {
        var selected = catalog ?? currentCatalog ?? SessionCatalog;
        if (catalogRules.TryGetValue(selected, out var rules))
            return rules;
        return selected.Equals(SessionCatalog, StringComparison.OrdinalIgnoreCase) ? SessionCatalogRules : UnconfiguredCatalogRules;
    }

    internal virtual void ValidateCatalogQualifier(string catalog, int namespaceCount)
    {
        if (namespaceCount == 0 && GetCatalogRules(catalog).MaxNamespaceParts == 1
            && catalog.Equals(SessionCatalog, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("An explicit Spark session catalog requires a namespace.");
    }

    private static void ValidateObjectComponent(string name, SparkCatalogNameRules rules)
    {
        if (rules.IsValidObjectComponent?.Invoke(name) == false)
            throw new ArgumentException($"'{name}' is not a valid table or namespace name in the selected catalog.");
    }
}
