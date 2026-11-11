using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Authentication;
using DubUrl.Conversion;
using DubUrl.Core.Testing.Conversion;
using DubUrl.Mapping;
using DubUrl.Provider.Odbc.Drivers.Discovery;
using DubUrl.Provider.Odbc.Mapping;
using DubUrl.Rewriting;
using NUnit.Framework;

namespace DubUrl.Provider.Odbc.Tests.Rewriting;

public sealed class SparkOdbcTest
{
    private static SchemeRegistry Registry()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        return new ProviderCatalogBuilder().AddApplicationProviders().Build();
    }

    [TestCase("/warehouse/0123456789abcdef", "/sql/1.0/warehouses/0123456789abcdef", null, null)]
    [TestCase("/warehouse/0123456789abcdef/qa", "/sql/1.0/warehouses/0123456789abcdef", "qa", null)]
    [TestCase("/warehouse/0123456789abcdef/qa/default", "/sql/1.0/warehouses/0123456789abcdef", "qa", "default")]
    [TestCase("/cluster/123456789/0405-060708-example", "/sql/protocolv1/o/123456789/0405-060708-example", null, null)]
    [TestCase("/cluster/123456789/0405-060708-example/qa", "/sql/protocolv1/o/123456789/0405-060708-example", "qa", null)]
    [TestCase("/cluster/123456789/0405-060708-example/qa/default", "/sql/protocolv1/o/123456789/0405-060708-example", "qa", "default")]
    [TestCase("/warehouse/qa/a%2Bb/%E6%95%B0%E6%8D%AE", "/sql/1.0/warehouses/qa", "a+b", "数据")]
    public void DatabricksRouteMapsEndpointAndSessionDefaults(string path, string httpPath, string? catalog, string? schema)
    {
        var url = new ConnectionUrl($"odbc+databricks://example.com{path}?Driver=Explicit%20ODBC", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings.Driver, Is.EqualTo("{Explicit ODBC}"));
            Assert.That(settings["Host"], Is.EqualTo("example.com"));
            Assert.That(settings["Port"], Is.EqualTo("443"));
            Assert.That(settings["HTTPPath"], Is.EqualTo(httpPath));
            Assert.That(settings["ThriftTransport"], Is.EqualTo("2"));
            Assert.That(settings["SSL"], Is.EqualTo("1"));
            Assert.That(settings.ContainsKey("EnableArrow"), Is.False);
            Assert.That(settings.TryGetValue("Catalog", out var actualCatalog) ? actualCatalog : null, Is.EqualTo(catalog));
            Assert.That(settings.TryGetValue("Schema", out var actualSchema) ? actualSchema : null, Is.EqualTo(schema));
        });
    }

    [TestCase("")]
    [TestCase("/sql/1.0/warehouses/qa")]
    [TestCase("/sql/protocolv1/o/123/0405-060708-example")]
    [TestCase("/unknown/qa")]
    [TestCase("/warehouse")]
    [TestCase("/warehouse//qa")]
    [TestCase("/warehouse/qa//schema")]
    [TestCase("/warehouse/qa/catalog/schema/extra")]
    [TestCase("/warehouse/qa%2Fextra")]
    [TestCase("/cluster/123")]
    [TestCase("/cluster/workspace/0405-060708-example")]
    [TestCase("/cluster/123/0405%2Fexample")]
    [TestCase("/cluster/123/0405-example/catalog/schema/extra")]
    [TestCase("/warehouse/qa?HTTPPath=%2Fcustom")]
    [TestCase("/warehouse/qa/catalog?catalog=override")]
    [TestCase("/warehouse/qa/catalog/schema?SCHEMA=override")]
    public void DatabricksRejectsInvalidPathsAndQueryOptions(string path)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var url = new ConnectionUrl($"odbc+databricks://example.com{path}{separator}Driver=Explicit", Registry());

        Assert.That(() => url.Parse(), Throws.InstanceOf<InvalidConnectionUrlException>());
    }

    [Test]
    public void DatabricksPreservesExplicitPortAndDriverOptions()
    {
        var url = new ConnectionUrl(
            "odbc+databricks://example.com:8443/warehouse/qa?Driver=Explicit&AuthMech=3&EnableArrow=1", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings.Driver, Is.EqualTo("{Explicit}"));
            Assert.That(settings["Port"], Is.EqualTo("8443"));
            Assert.That(settings["AuthMech"], Is.EqualTo("3"));
            Assert.That(settings["EnableArrow"], Is.EqualTo("1"));
        });
    }

    [TestCase("", "10000")]
    [TestCase(":10001", "10001")]
    public void SparkMapsPortsAndPreservesExplicitOptions(string port, string expected)
    {
        var url = new ConnectionUrl(
            $"odbc+spark://localhost{port}/unused?driver=Explicit&HTTPPath=%2Fcustom%2F&SSL=0&ThriftTransport=1&AuthMech=0&EnableArrow=1", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings.Driver, Is.EqualTo("{Explicit}"));
            Assert.That(settings["Port"], Is.EqualTo(expected));
            Assert.That(settings["HTTPPath"], Is.EqualTo("/custom/"));
            Assert.That(settings["SSL"], Is.EqualTo("0"));
            Assert.That(settings["ThriftTransport"], Is.EqualTo("1"));
            Assert.That(settings["AuthMech"], Is.EqualTo("0"));
            Assert.That(settings["EnableArrow"], Is.EqualTo("1"));
        });
    }

    [Test]
    public void SparkUsesStandardThriftResults()
    {
        var url = new ConnectionUrl("odbc+spark://localhost?Driver=Explicit", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings.ContainsKey("HTTPPath"), Is.False);
            Assert.That(settings["EnableArrow"], Is.EqualTo("0"));
            Assert.That(settings.ContainsKey("ThriftTransport"), Is.False);
            Assert.That(settings.ContainsKey("SSL"), Is.False);
        });
    }

    [Test]
    public void DatabricksPatUsesExistingRuntimeCredentialMapping()
    {
        var url = new ConnectionUrl(
            "odbc+databricks://example.com/warehouse/qa?Driver=Explicit&AuthMech=3", Registry())
            .WithAuthentication(ConnectionAuthentication.UsernamePassword("token", "qa-placeholder"));

        using var connection = url.Connect();
        var settings = new OdbcConnectionStringBuilder(connection.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
            Assert.That(settings["AuthMech"], Is.EqualTo("3"));
            Assert.That(settings["UID"], Is.EqualTo("token"));
            Assert.That(settings["PWD"], Is.EqualTo("qa-placeholder"));
        });
    }

    [TestCase("/sql/1.0/warehouses/qa", "", "/warehouse/qa")]
    [TestCase("/sql/1.0/warehouses/qa", ";Catalog=main", "/warehouse/qa/main")]
    [TestCase("/sql/protocolv1/o/123/0405-060708-example", ";Catalog=main;Schema=default",
        "/cluster/123/0405-060708-example/main/default")]
    public void DatabricksReverseMappingUsesEndpointSegments(string httpPath, string defaults, string path)
    {
        var settings = $"Driver={{Databricks ODBC Driver}};Host=example.com;Port=443;HTTPPath={httpPath};ThriftTransport=2;SSL=1{defaults}";
        var uri = ConversionTestSupport.RoundTrip("odbc+databricks", settings, OdbcFactory.Instance, ReverseMapper);

        Assert.That(uri.Segments, Is.EqualTo(path[1..].Split('/')));
        Assert.That(uri.Options.Keys, Does.Not.Contain("HTTPPath").And.Not.Contain("Catalog").And.Not.Contain("Schema"));
    }

    [TestCase("/sql/custom/qa", "")]
    [TestCase("/sql/1.0/warehouses/qa", ";Schema=default")]
    public void DatabricksReverseMappingRejectsUnrepresentableSettings(string httpPath, string defaults)
    {
        var registry = ConversionTestSupport.Registry("odbc+databricks", OdbcFactory.Instance, ReverseMapper);
        var converter = new ConnectionStringConverter(registry);
        var settings = $"Driver={{Databricks ODBC Driver}};Host=example.com;Port=443;HTTPPath={httpPath};ThriftTransport=2;SSL=1{defaults}";

        Assert.That(converter.TryConvert("odbc+databricks", settings, out _, out var diagnostic), Is.False);
        Assert.That(diagnostic!.Code, Is.EqualTo(ConnectionStringConversionDiagnosticCode.SettingsNotRepresentable));
    }

    private static SparkOdbcMapper ReverseMapper(DbConnectionStringBuilder builder)
        => new(builder, ConversionTestSupport.Dialect, ConversionTestSupport.Parametrizer, []);

    [Test]
    public void LocatorsSelectOnlyTheirOwnDriverWhenBothAreInstalled()
    {
        var drivers = new FakeDriverLister(["Simba Spark ODBC Driver", "Databricks ODBC Driver"]);

        Assert.Multiple(() =>
        {
            Assert.That(new SparkDriverLocator(drivers).Locate(), Is.EqualTo("Simba Spark ODBC Driver"));
            Assert.That(new DatabricksDriverLocator(drivers).Locate(), Is.EqualTo("Databricks ODBC Driver"));
        });
    }

    [Test]
    public void LocatorsRejectMissingDriversWithoutFallingBack()
    {
        Assert.Throws<DriverDiscoveryException>(() =>
            new DatabricksDriverLocator(new FakeDriverLister(["Simba Spark ODBC Driver"])).Locate());
        Assert.Throws<DriverDiscoveryException>(() =>
            new SparkDriverLocator(new FakeDriverLister(["Databricks ODBC Driver"])).Locate());
    }

    private sealed class FakeDriverLister(string[] drivers) : DriverLister
    {
        public override string[] List() => drivers;
    }
}
