using System.Data.Odbc;
using DubUrl.Authentication;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Databricks.QA.Connection;

public sealed class DatabricksOdbcTests
{
    [Test, Category("Live"), Category("ODBC")]
    public void OpenFreeEditionWarehouse()
    {
        var host = RequireVariable("DATABRICKS_HOST");
        var path = RequireVariable("DATABRICKS_HTTP_PATH");
        var token = RequireVariable("DATABRICKS_TOKEN");

        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            Assert.Fail("DATABRICKS_HOST must be the warehouse hostname.");
        const string warehousePrefix = "/sql/1.0/warehouses/";
        if (!path.StartsWith(warehousePrefix, StringComparison.Ordinal) || path.Length == warehousePrefix.Length
            || !path[warehousePrefix.Length..].All(char.IsAsciiLetterOrDigit))
            Assert.Fail("DATABRICKS_HTTP_PATH must be the warehouse HTTP path.");

        var url = new ConnectionUrl($"odbc+databricks://{host}{path}?AuthMech=3")
            .WithAuthentication(ConnectionAuthentication.UsernamePassword("token", token));
        OdbcSmokeTest.Verify(url, OdbcFactory.Instance);
    }

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Fail($"Set {name} before running Databricks QA.");
        return value!;
    }
}
