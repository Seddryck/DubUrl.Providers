using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Authentication;
using DubUrl.Provider.Odbc.Authentication;
using NUnit.Framework;

namespace DubUrl.Provider.Databricks.QA.Connection;

[Category("Live"), Category("ODBC")]
public sealed class DatabricksOAuthLiveTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ValidUserTokenOpensWarehouseAndQueries(bool asynchronous)
    {
        var target = CreateTarget(RequireOAuthToken());
        using var connection = asynchronous ? await target.OpenAsync() : target.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1";
        Assert.That(Convert.ToInt32(query.ExecuteScalar()), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InvalidUserTokenIsRejectedWithoutDisclosure(bool asynchronous)
    {
        _ = RequireOAuthToken();
        const string invalidToken = "invalid-databricks-oauth-token";
        var target = CreateTarget(invalidToken);
        var error = (await Assert.ThrowsAsync<ConnectionAuthenticationException>(async () =>
        {
            using var connection = asynchronous ? await target.OpenAsync() : target.Open();
        }))!;
        Assert.That(error.Code, Is.EqualTo(AuthenticationDiagnosticCodes.AuthenticationFailed));
        Assert.That(error.ToString().Contains(invalidToken, StringComparison.Ordinal), Is.False);
    }

    private static ConnectionUrl CreateTarget(string token)
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        return new ConnectionUrl(DatabricksWarehouse.Location)
            .WithAuthentication(DatabricksAuthentication.OAuthAccessToken(token));
    }

    private static string RequireOAuthToken()
    {
        var token = Environment.GetEnvironmentVariable("DATABRICKS_OAUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            Assert.Ignore("Set DATABRICKS_OAUTH_TOKEN to run Databricks OAuth warehouse QA.");
        return token!;
    }
}
