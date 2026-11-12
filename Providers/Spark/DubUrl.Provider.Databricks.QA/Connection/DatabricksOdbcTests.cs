using System.Data.Odbc;
using DubUrl.Authentication;
using DubUrl.Provider.Spark.QA;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Databricks.QA.Connection;

[Category("Live")]
public sealed class DatabricksOdbcTests : OdbcContract
{
    protected override string ConnectionUrl => DatabricksWarehouse.Url;
    protected override string AuthenticationConnectionUrl => DatabricksWarehouse.Location;
    protected override ConnectionUrl CreateLiveConnectionUrl()
        => base.CreateLiveConnectionUrl().WithAuthentication(DatabricksWarehouse.Authentication);
    protected override string SelectFirstCustomerSql => SparkOdbcCustomerQueries.First;
    protected override string SelectCustomerByIdSql => SparkOdbcCustomerQueries.ById;

    [Test, Category("ODBC")]
    public void OpenFreeEditionWarehouse()
        => OdbcSmokeTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance);

    [Test, Category("ODBC")]
    public void CreateDescribeAndDropUnityCatalogTable()
        => OdbcDdlTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance, "qa-table", "SeLeCt");
}

internal static class DatabricksWarehouse
{
    internal static ConnectionAuthentication Authentication
        => ConnectionAuthentication.UsernamePassword("token", RequireVariable("DATABRICKS_TOKEN"));

    internal static string Url => Location + "?AuthMech=3";

    internal static string Location
    {
        get
        {
            var host = RequireVariable("DATABRICKS_HOST");
            var path = RequireVariable("DATABRICKS_HTTP_PATH");

            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                Assert.Fail("DATABRICKS_HOST must be the warehouse hostname.");
            const string warehousePrefix = "/sql/1.0/warehouses/";
            if (!path.StartsWith(warehousePrefix, StringComparison.Ordinal) || path.Length == warehousePrefix.Length
                || !path[warehousePrefix.Length..].All(char.IsAsciiLetterOrDigit))
                Assert.Fail("DATABRICKS_HTTP_PATH must be the warehouse HTTP path.");

            return $"odbc+databricks://{host}{path}";
        }
    }

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Fail($"Set {name} before running Databricks QA.");
        return value!;
    }
}
