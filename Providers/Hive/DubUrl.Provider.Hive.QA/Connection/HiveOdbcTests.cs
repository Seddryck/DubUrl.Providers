using System.Data.Common;
using System.Data.Odbc;
using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Querying;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

public sealed class HiveOdbcTests
{
    [TestCase("HIVE_ODBC_HIVESERVER2_ENDPOINT", TestName = "OpenHiveServer2")]
    [TestCase("HIVE_ODBC_SPARK_ENDPOINT", TestName = "OpenSparkThriftServer")]
    [Category("Live"), Category("ODBC")]
    public void QueryThriftServer(string endpointVariable)
        => OdbcSmokeTest.Verify(CreateUrl(endpointVariable), OdbcFactory.Instance);

    [Test, Category("Live"), Category("ODBC")]
    public void ExecutesGeneratedHiveQlWithQuotedIdentifiers()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        using var database = new DatabaseUrl(CreateUrl("HIVE_ODBC_HIVESERVER2_ENDPOINT"), new(), NullQueryLogger.Instance);

        const string query = """
            SELECT $qualifiedColumn; format="identity"$
            FROM (SELECT 1 AS $column; format="identity"$) $source; format="identity"$
            """;
        var result = database.ReadScalar<int>(query, new Dictionary<string, object?>
        {
            ["qualifiedColumn"] = "sample.select",
            ["column"] = "select",
            ["source"] = "sample",
        });

        Assert.That(result, Is.EqualTo(1));
    }

    [Test, Category("Manifest")]
    public void ManifestIsValid()
        => QaManifest.Validate(Path.Combine(AppContext.BaseDirectory, "qa-manifest.json"),
            "hive", "Connection", "Querying", "ODBC");

    private static ConnectionUrl CreateUrl(string endpointVariable)
    {
        var endpoint = Environment.GetEnvironmentVariable(endpointVariable);
        Assert.That(endpoint, Is.Not.Null.And.Not.Empty, $"Set {endpointVariable} to the fixture's host:port.");
        return new ConnectionUrl($"odbc+hive://{endpoint}/default?AuthMech=0&ThriftTransport=0&SSL=0&UseNativeQuery=1");
    }
}
