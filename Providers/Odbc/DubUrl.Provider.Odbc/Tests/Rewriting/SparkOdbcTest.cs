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

    [TestCase("http", "", "", "10001", "/cliservice", null, "2", "0")]
    [TestCase("https", "/schema/analytics", "", "10001", "/cliservice", "analytics", "2", "1")]
    [TestCase("https", ":443/gateway/sandbox/spark/schema/analytics", "", "443", "/gateway/sandbox/spark", "analytics", "2", "1")]
    [TestCase("tcp", "", "", "10000", null, null, null, "0")]
    [TestCase("tcp", ":10003/schema/analytics", "&sasl=0", "10003", null, "analytics", "0", "0")]
    [TestCase("tcps", "/schema/analytics", "&SASL=1", "10000", null, "analytics", "1", "1")]
    [TestCase("tcps", "", "&ThriftTransport=1", "10000", null, null, "1", "1")]
    [TestCase("tcp", "", "&sasl=1&ThriftTransport=1&Schema=analytics", "10000", null, "analytics", "1", "0")]
    [TestCase("http", "/custom", "&SSL=0&ThriftTransport=2&HTTPPath=%2Fcustom", "10001", "/custom", null, "2", "0")]
    public void SparkTransportMapsEndpointAndSessionSettings(string suffix, string address, string options, string port,
        string? httpPath, string? schema, string? thriftTransport, string ssl)
    {
        var scheme = $"odbc+spark+{suffix}";
        var url = new ConnectionUrl($"{scheme}://example.com{address}?Driver=Explicit{options}", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings["Port"], Is.EqualTo(port));
            Assert.That(settings["SSL"], Is.EqualTo(ssl));
            Assert.That(settings["EnableArrow"], Is.EqualTo("0"));
            Assert.That(settings.TryGetValue("HTTPPath", out var path) ? path : null, Is.EqualTo(httpPath));
            Assert.That(settings.TryGetValue("Schema", out var actualSchema) ? actualSchema : null, Is.EqualTo(schema));
            Assert.That(settings.TryGetValue("ThriftTransport", out var thrift) ? thrift : null, Is.EqualTo(thriftTransport));
            Assert.That(settings.ContainsKey("sasl"), Is.False);
        });
    }

    [TestCase("http", "&sasl=1")]
    [TestCase("https", "&SSL=0")]
    [TestCase("http", "&ThriftTransport=0")]
    [TestCase("tcps", "&ThriftTransport=2")]
    [TestCase("tcp", "&sasl=2")]
    [TestCase("tcp", "&sasl=1&ThriftTransport=0")]
    [TestCase("tcps", "&HTTPPath=%2Fcliservice")]
    [TestCase("https", "&HTTPPath=%2Foverride")]
    [TestCase("https", "&%20SSL=0")]
    [TestCase("http+tcp", "")]
    public void SparkRejectsConflictingTransportOptions(string suffix, string options)
    {
        var url = new ConnectionUrl($"odbc+spark+{suffix}://example.com?Driver=Explicit{options}", Registry());

        Assert.That(() => url.Parse(), Throws.InstanceOf<InvalidConnectionUrlException>());
    }

    [TestCase("tcp", "/cliservice", "")]
    [TestCase("tcp", "/schema/%20", "")]
    [TestCase("http", "/cliservice/schema/%20", "")]
    [TestCase("http", "/endpoint/schema/one/schema/two", "")]
    [TestCase("https", "/schema/analytics", "&Schema=override")]
    public void SparkRejectsInvalidOrConflictingPathSettings(string suffix, string path, string options)
    {
        var url = new ConnectionUrl($"odbc+spark+{suffix}://example.com{path}?Driver=Explicit{options}", Registry());

        Assert.That(() => url.Parse(), Throws.InstanceOf<InvalidConnectionUrlException>());
    }

    [TestCase("http", "10001", ";HTTPPath=/cliservice;ThriftTransport=2;SSL=0", "", null)]
    [TestCase("https", "443", ";HTTPPath=/gateway/spark;Schema=analytics;ThriftTransport=2;SSL=1",
        "gateway/spark/schema/analytics", null)]
    [TestCase("tcp", "10000", ";Schema=analytics;SSL=0", "schema/analytics", null)]
    [TestCase("tcps", "10000", ";ThriftTransport=1;SSL=1", "", "1")]
    public void SparkReverseMappingUsesTransportScheme(string suffix, string port, string options, string path, string? sasl)
    {
        var settings = $"Driver={{Simba Spark ODBC Driver}};Host=example.com;Port={port};EnableArrow=0{options}";
        var registry = Registry();
        var uri = new ConnectionStringConverter(registry).Convert($"odbc+spark+{suffix}", settings);
        var actual = new OdbcConnectionStringBuilder(new ConnectionUrl(uri.ToString(false), registry).Parse());

        Assert.That(actual.EquivalentTo(new OdbcConnectionStringBuilder(settings)), Is.True);

        Assert.That(string.Join('/', uri.Segments), Is.EqualTo(path));
        Assert.That(uri.Options.TryGetValue("sasl", out var value) ? value : null, Is.EqualTo(sasl));
    }

    [Test]
    public void SparkReverseMappingRejectsReservedEndpointSuffix()
    {
        const string scheme = "odbc+spark+http";
        const string settings = "Driver={Simba Spark ODBC Driver};Host=example.com;Port=10001;EnableArrow=0;SSL=0;ThriftTransport=2;HTTPPath=/gateway/schema/analytics";
        var converter = new ConnectionStringConverter(Registry());

        Assert.That(converter.TryConvert(scheme, settings, out _, out var diagnostic), Is.False);
        Assert.That(diagnostic!.Code, Is.EqualTo(ConnectionStringConversionDiagnosticCode.SettingsNotRepresentable));
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
