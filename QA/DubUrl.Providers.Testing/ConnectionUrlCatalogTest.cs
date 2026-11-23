using System.Data.Common;
using System.Data.Odbc;
using System.Text.Json;
using DubUrl.BulkCopy;
using DubUrl.Discovery;
using DubUrl.Mapping;
using MySqlConnector;
using NReco.PrestoAdo;
using Npgsql;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public sealed class ConnectionUrlCatalogTest
{
    private JsonDocument catalog = null!;
    private SchemeRegistry registry = null!;

    [OneTimeSetUp]
    public void ReadCatalog()
    {
        using var stream = typeof(ConnectionUrlCatalogTest).Assembly.GetManifestResourceStream("DubUrl.ConnectionUrlCatalog")
            ?? throw new InvalidOperationException("Missing generated connection URL catalog.");
        catalog = JsonDocument.Parse(stream);
        DbProviderFactories.RegisterFactory("NReco.PrestoAdo", PrestoDbFactory.Instance);
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        DbProviderFactories.RegisterFactory("MySqlConnector", MySqlConnectorFactory.Instance);
        DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);
        registry = new ProviderCatalogBuilder().AddApplicationProviders().Build();
    }

    [OneTimeTearDown]
    public void DisposeCatalog() => catalog?.Dispose();

    [TestCase("default", "HTTPS", true, 8080, "")]
    [TestCase("verified", "HTTPS", true, 8443, ":8443")]
    [TestCase("http", "HTTP", false, 8080, "")]
    public void NativeTrinoUsesCatalogTlsModesAndPortDefaults(string modeId, string transport, bool useSsl, int port, string portSuffix)
    {
        var route = Route("nreco");
        var tls = catalog.RootElement.GetProperty("tlsPolicies").GetProperty(route.GetProperty("tls").GetString()!);
        var mode = tls.GetProperty("modes").EnumerateArray().Single(item => item.GetProperty("id").GetString() == modeId);
        var authority = $"alice@trino.example.com{portSuffix}/analytics/public";
        var url = new ConnectionUrl($"{route.GetProperty("scheme").GetString()}://{authority}{Query(mode)}", registry);
        var settings = new PrestoConnectionStringBuilder { ConnectionString = url.Parse() };

        Assert.Multiple(() =>
        {
            Assert.That(route.GetProperty("capabilities").GetProperty("tlsConfiguration").GetString(), Is.EqualTo("Trino"));
            Assert.That(mode.GetProperty("transport").GetString(), Is.EqualTo(transport));
            Assert.That(route.GetProperty("fields").GetProperty("port").GetProperty("default").GetInt32(), Is.EqualTo(8080));
            Assert.That(settings.UseSsl, Is.EqualTo(useSsl));
            Assert.That(settings.Port, Is.EqualTo(port));
            Assert.That(settings.Host, Is.EqualTo("trino.example.com"));
            Assert.That(settings.User, Is.EqualTo("alice"));
            Assert.That(settings["TrinoHeaders"], Is.EqualTo("1"));
        });
    }

    [Test]
    public void DatabricksPatUsesWarehouseRouteAndItsRegisteredCapabilities()
    {
        var route = Route("databricks-odbc");
        const string method = "databricksPat";
        Assert.That(route.GetProperty("auth").EnumerateArray().Select(item => item.GetString()), Does.Contain(method));
        var auth = catalog.RootElement.GetProperty("authentication").GetProperty(method);
        var user = auth.GetProperty("fixedUser").GetString();
        var authority = $"{user}@databricks.example.com/warehouse/123/main/reporting";
        var url = new ConnectionUrl($"{route.GetProperty("scheme").GetString()}://{authority}{Query(auth)}&Driver=Databricks%20ODBC%20Driver", registry);
        var settings = new OdbcConnectionStringBuilder(url.Parse());
        var capabilities = route.GetProperty("capabilities");

        Assert.Multiple(() =>
        {
            Assert.That(auth.GetProperty("via").GetString(), Is.EqualTo("runtime"));
            Assert.That(settings["UID"], Is.EqualTo("token"));
            Assert.That(settings["AuthMech"], Is.EqualTo("3"));
            Assert.That(settings["HTTPPath"], Is.EqualTo("/sql/1.0/warehouses/123"));
            Assert.That(settings["Catalog"], Is.EqualTo("main"));
            Assert.That(settings["Schema"], Is.EqualTo("reporting"));
            Assert.That(capabilities.GetProperty("schemaDiscovery").GetBoolean(), Is.True);
            Assert.That(url.TryGetCapability<SchemaDiscoveryCapability>(out _), Is.True);
            Assert.That(capabilities.GetProperty("fileLoad").GetBoolean(), Is.True);
            Assert.That(url.TryGetCapability<FileLoadCapability>(out _), Is.True);
        });
    }

    [TestCase("sparkKerberos", "1")]
    [TestCase("thriftPassword", "3")]
    public void SparkTcpAuthenticationProfilesSelectSasl(string method, string authMech)
    {
        var route = Route("spark-odbc");
        var profile = route.GetProperty("auth").EnumerateArray().Single(item =>
            item.ValueKind == JsonValueKind.Object && item.GetProperty("id").GetString() == method);
        var scheme = route.GetProperty("schemeByLocation").GetProperty("thriftTcp").GetProperty("on").GetString();
        var auth = catalog.RootElement.GetProperty("authentication").GetProperty(method);
        var sasl = profile.GetProperty("queryByLocation").GetProperty("thriftTcp").GetProperty("sasl").GetString();
        var url = new ConnectionUrl($"{scheme}://spark.example{Query(auth)}&sasl={sasl}&Driver=Simba%20Spark%20ODBC%20Driver", registry);
        var settings = new OdbcConnectionStringBuilder(url.Parse());

        Assert.That(auth.GetProperty("via").GetString(), Is.EqualTo("runtime"));
        if (method == "sparkKerberos")
            Assert.That(auth.GetProperty("library").GetString(), Is.EqualTo("ConnectionAuthentication.OperatingSystemIdentity()"));
        Assert.That(settings["AuthMech"], Is.EqualTo(authMech));
        Assert.That(settings["ThriftTransport"], Is.EqualTo("1"));
        Assert.That(settings["SSL"], Is.EqualTo("1"));
    }

    [Test]
    public void CatalogSchemesRequireVerifiedTls()
    {
        var mysql = Route("mysqlconnector");
        var certificate = catalog.RootElement.GetProperty("authentication").GetProperty("certificate");
        var mysqlUrl = new ConnectionUrl(VerifiedTlsScheme(mysql, mysql.GetProperty("tls")) + "://alice@mysql.example.com/app", registry);
        var postgres = Route("npgsql");
        var postgresUrl = new ConnectionUrl(VerifiedTlsScheme(postgres, certificate.GetProperty("tlsOverride"))
            + "://alice@postgres.example.com/app", registry);

        Assert.Multiple(() =>
        {
            Assert.That(new MySqlConnectionStringBuilder(mysqlUrl.Parse()).SslMode, Is.EqualTo(MySqlSslMode.VerifyFull));
            Assert.That(new NpgsqlConnectionStringBuilder(postgresUrl.Parse()).SslMode, Is.EqualTo(SslMode.VerifyFull));
        });
    }

    private string VerifiedTlsScheme(JsonElement route, JsonElement policy)
    {
        var modes = catalog.RootElement.GetProperty("tlsPolicies").GetProperty(policy.GetString()!).GetProperty("modes");
        var verified = modes.EnumerateArray().Single(mode => mode.GetProperty("id").GetString() == "verified");
        Assert.That(verified.GetProperty("via").GetString(), Is.EqualTo("scheme"));
        return route.GetProperty("scheme").GetString() + verified.GetProperty("schemeSuffix").GetString();
    }

    private JsonElement Route(string id)
        => catalog.RootElement.GetProperty("databases").EnumerateArray()
            .SelectMany(database => database.GetProperty("routes").EnumerateArray())
            .Single(route => route.GetProperty("id").GetString() == id);

    private static string Query(JsonElement option)
        => option.TryGetProperty("query", out var query)
            ? "?" + string.Join("&", query.EnumerateObject().Select(property =>
                $"{Uri.EscapeDataString(property.Name)}={Uri.EscapeDataString(property.Value.GetString()!)}"))
            : "";
}
