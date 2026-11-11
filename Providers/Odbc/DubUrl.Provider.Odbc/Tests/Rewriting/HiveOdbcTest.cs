using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Core.Testing.Conversion;
using DubUrl.Locating.OdbcDriver;
using DubUrl.Mapping;
using DubUrl.Provider.Hive.Sql;
using DubUrl.Provider.Odbc.Drivers.Discovery;
using DubUrl.Provider.Odbc.Mapping;
using DubUrl.Querying.Parametrizing;
using NUnit.Framework;

namespace DubUrl.Provider.Odbc.Tests.Rewriting;

public sealed class HiveOdbcTest
{
    [TestCase("select", "`select`")]
    [TestCase("has`tick", "`has``tick`")]
    [TestCase("analytics.order details.select", "`analytics`.`order details`.`select`")]
    [TestCase("analytics.has`tick", "`analytics`.`has``tick`")]
    public void HiveRouteQuotesIdentifiers(string identifier, string expected)
    {
        var url = new ConnectionUrl("odbc+hive://localhost?Driver=Explicit", Registry());

        Assert.That(url.Dialect, Is.TypeOf<HiveDialect>());
        Assert.That(url.Dialect.Renderer.Render(identifier, "identity"), Is.EqualTo(expected));
    }

    [TestCase("example.com/analytics", "10000", "analytics")]
    [TestCase("example.com:10001/my%20database", "10001", "my database")]
    public void MapsEndpointAndPreservesDriverOptions(string endpoint, string port, string schema)
    {
        var url = new ConnectionUrl(
            $"odbc+hive://hive-user:p%40ss@{endpoint}?Driver=Explicit%20ODBC&AuthMech=3&ThriftTransport=2&HTTPPath=%2Fcliservice", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(url.Parametrizer, Is.TypeOf<PositionalParametrizer>());
            Assert.That(settings.Driver, Is.EqualTo("{Explicit ODBC}"));
            Assert.That(settings["Host"], Is.EqualTo("example.com"));
            Assert.That(settings["Port"], Is.EqualTo(port));
            Assert.That(settings["Schema"], Is.EqualTo(schema));
            Assert.That(settings["HiveServerType"], Is.EqualTo("2"));
            Assert.That(settings["UID"], Is.EqualTo("hive-user"));
            Assert.That(settings["PWD"], Is.EqualTo("p@ss"));
            Assert.That(settings["AuthMech"], Is.EqualTo("3"));
            Assert.That(settings["ThriftTransport"], Is.EqualTo("2"));
            Assert.That(settings["HTTPPath"], Is.EqualTo("/cliservice"));
        });
    }

    [TestCase("odbc+hive://localhost?Driver=Explicit")]
    [TestCase("odbc+hive://localhost/?Driver=Explicit")]
    public void OmittedSchemaUsesDriverDefault(string address)
    {
        var settings = new OdbcConnectionStringBuilder(new ConnectionUrl(address, Registry()).Parse());
        Assert.That(settings.ContainsKey("Schema"), Is.False);
    }

    [Test]
    public void ReverseMappingPreservesSchemaAndDriverOptions()
    {
        const string settings = "Driver={Cloudera ODBC Driver for Apache Hive};Host=example.com;Port=10001;Schema=analytics;HiveServerType=2;AuthMech=0";
        ConversionTestSupport.RoundTrip("odbc+hive", settings, OdbcFactory.Instance,
            builder => new HiveOdbcMapper(builder, ConversionTestSupport.Dialect, ConversionTestSupport.Parametrizer, []));
    }

    [Test]
    public void DiscoversRegisteredHiveDriver()
    {
        var locator = new HiveDriverLocator(new FakeDriverLister(["Simba Spark ODBC Driver",
            "Cloudera ODBC Driver for Apache Hive"]));
        Assert.That(locator.Locate(), Is.EqualTo("Cloudera ODBC Driver for Apache Hive"));
    }

    [Test]
    public void RejectsSparkDriverWhenHiveDriverIsMissing()
    {
        var locator = new HiveDriverLocator(new FakeDriverLister(["Simba Spark ODBC Driver"]));
        Assert.Throws<DriverDiscoveryException>(() => locator.Locate());
    }

    private static SchemeRegistry Registry()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        return new ProviderCatalogBuilder().AddApplicationProviders().Build();
    }

    private sealed class FakeDriverLister(string[] drivers) : DriverLister
    {
        public override string[] List() => drivers;
    }
}
