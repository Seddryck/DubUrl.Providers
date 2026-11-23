using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Conversion;
using DubUrl.Core.Testing.Conversion;
using DubUrl.Locating.OdbcDriver;
using DubUrl.Mapping;
using DubUrl.Provider.Hive.Sql;
using DubUrl.Provider.Odbc.Drivers.Discovery;
using DubUrl.Provider.Odbc.Mapping;
using DubUrl.Querying.Parametrizing;
using DubUrl.Rewriting;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Odbc.Tests.Rewriting;

public sealed class HiveOdbcTest
{
    [Test]
    public void HiveScriptsEmitAnnotationsAndRejectManagedProperties()
    {
        var url = new ConnectionUrl("odbc+hive://localhost?Driver=Explicit", Registry());
        var table = Annotated("myapp.owner");
        var script = url.ScriptSchema(new Schema.Schema([table]));
        const string expected = """ALTER TABLE `sales`.`items` SET TBLPROPERTIES ('myapp.owner' = 'team\'s\\data');""";

        Assert.That(script, Does.StartWith("CREATE TABLE").And.EndWith(expected));
        foreach (var key in new[] { "transactional", "orc.compress", "last_modified_time" })
        {
            var schema = new Schema.Schema([Annotated(key)]);
            Assert.That(() => url.ScriptSchema(schema), Throws.TypeOf<NotSupportedException>().With.Message.Contains(key));
        }

        static Table Annotated(string key) => new(new DatabaseObjectName("items", "sales"), [new("id", DbType.Int32)],
            null, null, null, new Dictionary<string, string> { [key] = @"team's\data" });
    }

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

    [TestCase("http", "", "", "10001", "/cliservice", null, "2", "0")]
    [TestCase("https", "/schema/analytics", "", "10001", "/cliservice", "analytics", "2", "1")]
    [TestCase("https", ":443/gateway/sandbox/hive/schema/my%20db", "", "443", "/gateway/sandbox/hive", "my db", "2", "1")]
    [TestCase("tcp", "", "", "10000", null, null, null, "0")]
    [TestCase("tcp", "/schema/analytics", "&sasl=0", "10000", null, "analytics", "0", "0")]
    [TestCase("tcps", "", "&sasl=1&ThriftTransport=1", "10000", null, null, "1", "1")]
    [TestCase("tcps", "", "&ThriftTransport=1", "10000", null, null, "1", "1")]
    public void MapsExplicitTransport(string suffix, string address, string options, string port,
        string? httpPath, string? schema, string? thriftTransport, string ssl)
    {
        var url = new ConnectionUrl(
            $"odbc+hive+{suffix}://example.com{address}?Driver=Explicit&UseNativeQuery=0{options}", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(url.Dialect, Is.TypeOf<HiveDialect>());
            Assert.That(settings.Driver, Is.EqualTo("{Explicit}"));
            Assert.That(settings["Port"], Is.EqualTo(port));
            Assert.That(settings["SSL"], Is.EqualTo(ssl));
            Assert.That(settings["HiveServerType"], Is.EqualTo("2"));
            Assert.That(settings["UseNativeQuery"], Is.EqualTo("0"));
            Assert.That(settings.TryGetValue("HTTPPath", out var path) ? path : null, Is.EqualTo(httpPath));
            Assert.That(settings.TryGetValue("Schema", out var database) ? database : null, Is.EqualTo(schema));
            Assert.That(settings.TryGetValue("ThriftTransport", out var thrift) ? thrift : null, Is.EqualTo(thriftTransport));
            Assert.That(settings.ContainsKey("sasl"), Is.False);
        });
    }

    [TestCase("http", "", "&sasl=0")]
    [TestCase("https", "", "&SSL=0")]
    [TestCase("http", "", "&ThriftTransport=1")]
    [TestCase("http", "", "&HTTPPath=other")]
    [TestCase("https", "/gateway/schema/one/schema/two", "")]
    [TestCase("tcp", "/cliservice", "")]
    [TestCase("tcp", "", "&HTTPPath=cliservice")]
    [TestCase("tcps", "", "&ThriftTransport=2")]
    [TestCase("tcps", "", "&sasl=1&ThriftTransport=0")]
    [TestCase("tcps", "", "&sasl=2")]
    [TestCase("tcps", "/schema/analytics", "&Schema=other")]
    [TestCase("tcp+https", "", "")]
    public void RejectsConflictingTransportOrPath(string suffix, string path, string options)
    {
        var url = new ConnectionUrl($"odbc+hive+{suffix}://localhost{path}?Driver=Explicit{options}", Registry());
        Assert.That(() => url.Parse(), Throws.InstanceOf<InvalidConnectionUrlException>());
    }

    [TestCase("http", "10001", ";HTTPPath=/cliservice;ThriftTransport=2;SSL=0")]
    [TestCase("https", "443", ";HTTPPath=/gateway/hive;Schema=analytics;ThriftTransport=2;SSL=1")]
    [TestCase("tcp", "10000", ";Schema=analytics;ThriftTransport=0;SSL=0")]
    [TestCase("tcps", "10000", ";ThriftTransport=1;SSL=1")]
    public void RoundTripsExplicitTransport(string suffix, string port, string options)
    {
        var settings = $"Driver={{Cloudera ODBC Driver for Apache Hive}};Host=example.com;Port={port};HiveServerType=2{options}";
        var registry = Registry();
        var uri = new ConnectionStringConverter(registry).Convert($"odbc+hive+{suffix}", settings);
        var actual = new OdbcConnectionStringBuilder(new ConnectionUrl(uri.ToString(false), registry).Parse());

        Assert.That(actual.EquivalentTo(new OdbcConnectionStringBuilder(settings)), Is.True);
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
