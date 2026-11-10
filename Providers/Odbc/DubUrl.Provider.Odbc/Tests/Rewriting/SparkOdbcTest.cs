using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Authentication;
using DubUrl.Core.Testing.Conversion;
using DubUrl.Mapping;
using DubUrl.Provider.Odbc.Drivers.Discovery;
using DubUrl.Provider.Odbc.Mapping;
using NUnit.Framework;

namespace DubUrl.Provider.Odbc.Tests.Rewriting;

public sealed class SparkOdbcTest
{
    private static SchemeRegistry Registry()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        return new ProviderCatalogBuilder().AddApplicationProviders().Build();
    }

    [TestCase("odbc+databricks", "/sql/1.0/warehouses/0123456789abcdef")]
    [TestCase("odbc+databricks", "/sql/protocolv1/o/123456789/0405-060708-example")]
    [TestCase("odbc+databricks", "/sql//custom/path")]
    public void DatabricksRoutePreservesPathAndSeparateOptions(string scheme, string path)
    {
        var url = new ConnectionUrl(
            $"{scheme}://example.com{path}?Driver=Explicit%20ODBC&Catalog=qa&Schema=default", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings.Driver, Is.EqualTo("{Explicit ODBC}"));
            Assert.That(settings["Host"], Is.EqualTo("example.com"));
            Assert.That(settings["Port"], Is.EqualTo("443"));
            Assert.That(settings["HTTPPath"], Is.EqualTo(path));
            Assert.That(settings["ThriftTransport"], Is.EqualTo("2"));
            Assert.That(settings["SSL"], Is.EqualTo("1"));
            Assert.That(settings["Catalog"], Is.EqualTo("qa"));
            Assert.That(settings["Schema"], Is.EqualTo("default"));
        });
    }

    [TestCase("odbc+databricks", "", "443")]
    [TestCase("odbc+databricks", ":8443", "8443")]
    [TestCase("odbc+spark", "", "10000")]
    [TestCase("odbc+spark", ":10001", "10001")]
    public void RouteMapsPortsAndPreservesExplicitOptions(string scheme, string port, string expected)
    {
        var url = new ConnectionUrl(
            $"{scheme}://localhost{port}/unused?driver=Explicit&HTTPPath=%2Fcustom%2F&SSL=0&ThriftTransport=1&AuthMech=0", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings.Driver, Is.EqualTo("{Explicit}"));
            Assert.That(settings["Port"], Is.EqualTo(expected));
            Assert.That(settings["HTTPPath"], Is.EqualTo("/custom/"));
            Assert.That(settings["SSL"], Is.EqualTo("0"));
            Assert.That(settings["ThriftTransport"], Is.EqualTo("1"));
            Assert.That(settings["AuthMech"], Is.EqualTo("0"));
        });
    }

    [Test]
    public void SparkDoesNotApplyDatabricksDefaults()
    {
        var url = new ConnectionUrl("odbc+spark://localhost?Driver=Explicit", Registry());
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.Multiple(() =>
        {
            Assert.That(settings.ContainsKey("HTTPPath"), Is.False);
            Assert.That(settings.ContainsKey("ThriftTransport"), Is.False);
            Assert.That(settings.ContainsKey("SSL"), Is.False);
        });
    }

    [Test]
    public void DatabricksPatUsesExistingRuntimeCredentialMapping()
    {
        var url = new ConnectionUrl(
            "odbc+databricks://example.com/sql/1.0/warehouses/qa?Driver=Explicit&AuthMech=3", Registry())
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

    [Test]
    public void ReverseMappingPreservesDriverOptions()
    {
        const string settings = "Driver={Databricks ODBC Driver};Host=example.com;Port=443;HTTPPath=/sql//qa/;ThriftTransport=2;SSL=1;Schema=default";
        ConversionTestSupport.RoundTrip("odbc+databricks", settings, OdbcFactory.Instance,
            builder => new SparkOdbcMapper(builder, ConversionTestSupport.Dialect, ConversionTestSupport.Parametrizer, []));
    }

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
