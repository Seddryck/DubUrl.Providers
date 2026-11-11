using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Mapping;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.Tests;

public sealed class SparkSqlDialectTest
{
    [TestCase("odbc+spark", "", typeof(SparkSqlDialect))]
    [TestCase("odbc+databricks", "/warehouse/qa", typeof(DatabricksSqlDialect))]
    public void SchemeSelectsDialectEvenWithExplicitDriver(string scheme, string path, Type expected)
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        var registry = new ProviderCatalogBuilder().AddApplicationProviders().Build();
        var url = new ConnectionUrl($"{scheme}://example.com{path}?Driver=Explicit", registry);

        Assert.That(url.Dialect.GetType(), Is.EqualTo(expected));
        Assert.That(DefaultRegistry.GetDialects(scheme).Get(scheme).GetType(), Is.EqualTo(expected));
    }

    [Test]
    public void SparkSessionCatalogPreservesSpellingAndLeavesOmittedQualifiersToSession()
    {
        var dialect = Spark();
        Assert.That(dialect.RenderObjectName("SELECT", []), Is.EqualTo("`SELECT`"));
        Assert.That(dialect.RenderObjectName("SELECT", ["MySchema"]), Is.EqualTo("`MySchema`.`SELECT`"));
        Assert.That(dialect.RenderObjectName("SELECT", ["MySchema"], "spark_catalog"),
            Is.EqualTo("`spark_catalog`.`MySchema`.`SELECT`"));
        Assert.That(() => dialect.RenderObjectName("a.b", []), Throws.ArgumentException);
        Assert.That(() => dialect.RenderObjectName("t", ["one", "two"]), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.RenderObjectName("t", [], "spark_catalog"), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void SparkPluginRulesPreserveLiteralComponentBoundariesAndCatalogCaseSensitivity()
    {
        var dialect = Spark().WithCatalogRules("plugin", new(StringComparer.Ordinal, 2), isCurrent: true);
        Assert.That(dialect.RenderObjectName("table`name", ["outer.ns", "Inner"]),
            Is.EqualTo("`outer.ns`.`Inner`.`table``name`"));
        Assert.That(dialect.RenderObjectName("a.b", ["ns"], "plugin"), Is.EqualTo("`plugin`.`ns`.`a.b`"));
        Assert.That(() => dialect.RenderObjectName("t", ["one", "two", "three"]), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.ValidateObjectNames([new("Name"), new("name")]), Throws.Nothing);
        Assert.That(() => dialect.ValidateObjectNames([new("Name", "default", Catalog: "spark_catalog"), new("name", "default", Catalog: "spark_catalog")]), Throws.ArgumentException);
        Assert.That(() => Spark().ValidateObjectNames([new("Name"), new("name")]), Throws.ArgumentException);
    }

    [Test]
    public void DatabricksValidatesSecurablesAndQuotesLiteralColumns()
    {
        var dialect = Databricks();
        Assert.That(dialect, Is.InstanceOf<SparkSqlDialect>());
        Assert.That(dialect.RenderObjectName("select", []), Is.EqualTo("`select`"));
        Assert.That(dialect.RenderObjectName("qa-table", ["Schema"]), Is.EqualTo("`Schema`.`qa-table`"));
        Assert.That(dialect.RenderObjectName("qa-table", ["Schema"], "Catalog"), Is.EqualTo("`Catalog`.`Schema`.`qa-table`"));
        Assert.That(dialect.SchemaSqlRenderer.RenderIdentifier(dialect, "Col.Name`"), Is.EqualTo("`Col.Name```"));
        Assert.That(() => dialect.RenderObjectName(new string('x', 255), []), Throws.Nothing);
        Assert.That(() => dialect.RenderObjectName(new string('x', 256), []), Throws.ArgumentException);
        Assert.That(() => dialect.RenderObjectName("t", [], "Catalog"), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.RenderObjectName("t", ["s", "nested"]), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.RenderObjectName("t", ["s"], "bad.catalog"), Throws.ArgumentException);
        Assert.That(() => dialect.RenderObjectName("t", ["bad/schema"]), Throws.ArgumentException);
        Assert.That(() => dialect.ValidateObjectNames([new("table", "Schema", Catalog: "Catalog"),
            new("TABLE", "schema", Catalog: "catalog")]), Throws.ArgumentException);
        Assert.That(() => dialect.ValidateObjectNames([new("table", "one"), new("TABLE", "two")]), Throws.Nothing);
    }

    [TestCase("a.b")]
    [TestCase("a b")]
    [TestCase("a/b")]
    [TestCase("a\u001Fb")]
    [TestCase("a\u007Fb")]
    public void UnityCatalogRejectsForbiddenObjectCharacters(string name)
        => Assert.That(() => Databricks().RenderObjectName(name, []), Throws.ArgumentException);

    [Test]
    public void TablePlanningChecksColumnCollisionsAndUsesSharedRendering()
    {
        var dialect = Databricks();
        var name = new DatabaseObjectName("qa-table", "qa", catalog: "catalog");
        var model = new DubUrl.Schema.Schema([new Table(name, [new Column("Col.Name`", DbType.Int32)])]);
        var sql = new SchemaScriptRenderer(dialect).Plan(model).Single().CommandText;
        Assert.That(sql, Does.Contain("CREATE TABLE `catalog`.`qa`.`qa-table`").And.Contain("`Col.Name```"));

        var context = new TableDefinitionContext(new("t", "qa"), ["Name", "name"]);
        var renderer = (ITableDefinitionSqlRenderer)dialect.SchemaSqlRenderer;
        Assert.That(() => renderer.RenderTableDefinition(dialect, context), Throws.ArgumentException);
        var plugin = Spark().WithCatalogRules("plugin", new(StringComparer.Ordinal), isCurrent: true);
        Assert.That(() => renderer.RenderTableDefinition(plugin, context), Throws.Nothing);
    }

    [Test]
    public void DatabricksRejectsDatabaseQualifier()
    {
        var dialect = Databricks();
        Assert.That(() => dialect.SchemaSqlRenderer.RenderObjectName(dialect, null, "database", "schema", "table"),
            Throws.TypeOf<NotSupportedException>());
    }

    private static SparkSqlDialect Spark() => DefaultRegistry.GetDialects("odbc+spark").Get<SparkSqlDialect>();
    private static DatabricksSqlDialect Databricks() => DefaultRegistry.GetDialects("odbc+databricks").Get<DatabricksSqlDialect>();
}
