using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Authentication;
using DubUrl.Mapping;
using DubUrl.Provider.Odbc.Authentication;
using DubUrl.Rewriting;
using NUnit.Framework;

namespace DubUrl.Provider.Odbc.Tests.Rewriting;

public sealed class DatabricksOAuthTests
{
    private const string Token = "oauth-access-token-sentinel";
    private const string Driver = "Driver=Databricks%20ODBC%20Driver";
    private const string Endpoint = "example.com:8443/warehouse/qa/main/reporting";
    private const string Location = "odbc+databricks://" + Endpoint + "?" + Driver;

    [TestCase(false)]
    [TestCase(true)]
    public async Task PreparesTokenPassThroughWithoutChangingUrl(bool asynchronous)
    {
        var authentication = DatabricksAuthentication.OAuthAccessToken(Token);
        var url = Target(Location + "&EnableArrow=1", authentication);
        url.ValidateAuthentication();
        using var connection = asynchronous ? await url.ConnectAsync() : url.Connect();
        var settings = new OdbcConnectionStringBuilder(connection.ConnectionString);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Driver, Is.EqualTo("{Databricks ODBC Driver}"));
            Assert.That(settings["Host"], Is.EqualTo("example.com"));
            Assert.That(settings["Port"], Is.EqualTo("8443"));
            Assert.That(settings["HTTPPath"], Is.EqualTo("/sql/1.0/warehouses/qa"));
            Assert.That(settings["Catalog"], Is.EqualTo("main"));
            Assert.That(settings["Schema"], Is.EqualTo("reporting"));
            Assert.That(settings["EnableArrow"], Is.EqualTo("1"));
            Assert.That(settings["SSL"], Is.EqualTo("1"));
            Assert.That(settings["ThriftTransport"], Is.EqualTo("2"));
            Assert.That(settings["AuthMech"], Is.EqualTo("11"));
            Assert.That(settings["Auth_Flow"], Is.EqualTo("0"));
            Assert.That(settings["LogLevel"], Is.EqualTo("0"));
            Assert.That(Equals(settings["Auth_AccessToken"], Token), Is.True);
            Assert.That(settings.ContainsKey("PWD"), Is.False);
            Assert.That(settings.ContainsKey("UID"), Is.False);
            Assert.That(url.Parse().Contains("Auth_AccessToken", StringComparison.OrdinalIgnoreCase), Is.False);
            Assert.That(url.Url.Contains(Token, StringComparison.Ordinal), Is.False);
            Assert.That(authentication.ToString().Contains(Token, StringComparison.Ordinal), Is.False);
        });
    }

    [TestCase(Location + "&AuthMech=3")]
    [TestCase(Location + "&Auth_Flow=1")]
    [TestCase(Location + "&Auth_Flow=2")]
    [TestCase(Location + "&Auth_Client_ID=client")]
    [TestCase(Location + "&Auth_AccessToken=" + Token)]
    [TestCase(Location + "&PWD=" + Token)]
    [TestCase(Location + "&SSL=0")]
    [TestCase(Location + "&ThriftTransport=1")]
    [TestCase(Location + "&AllowSelfSignedServerCert=1")]
    [TestCase(Location + "&LogLevel=6")]
    [TestCase("odbc+databricks://token@" + Endpoint + "?" + Driver)]
    public void RejectsConflictingSettings(string location)
    {
        var url = Target(location, DatabricksAuthentication.OAuthAccessToken(Token));
        var error = Assert.Throws<ConnectionAuthenticationException>(() => url.Connect())!;
        Assert.That(error.Code, Is.EqualTo(AuthenticationDiagnosticCodes.CredentialConflict));
        Assert.That(error.ToString().Contains(Token, StringComparison.Ordinal), Is.False);
    }

    [TestCase("odbc+databricks://" + Endpoint + "?Driver=Different")]
    [TestCase("odbc+spark://example.com?" + Driver)]
    public void RequiresDatabricksRouteAndDriver(string location)
        => Assert.That(() => Target(location, DatabricksAuthentication.OAuthAccessToken(Token)).Connect(),
            Throws.TypeOf<ConnectionAuthenticationException>().With.Property("Code")
                .EqualTo(AuthenticationDiagnosticCodes.UnsupportedAuthentication));

    [Test]
    public void RejectsAnAccessTokenInTheUrl()
    {
        var url = new ConnectionUrl(Location + "&Auth%5FAccessToken=" + Token, Registry());
        var error = Assert.Throws<InvalidConnectionUrlException>(() => url.Parse())!;
        Assert.That(error.ToString().Contains(Token, StringComparison.Ordinal), Is.False);
    }

    private static ConnectionUrl Target(string location, ConnectionAuthentication authentication)
        => new ConnectionUrl(location, Registry()).WithAuthentication(authentication);

    private static SchemeRegistry Registry()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        return new ProviderCatalogBuilder().AddApplicationProviders().Build();
    }
}
