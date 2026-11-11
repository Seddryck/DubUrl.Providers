using System.Data.Odbc;
using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

public sealed class HiveOdbcTests
{
    [TestCase("HIVE_ODBC_HIVESERVER2_ENDPOINT", TestName = "OpenHiveServer2")]
    [TestCase("HIVE_ODBC_SPARK_ENDPOINT", TestName = "OpenSparkThriftServer")]
    [Category("Live"), Category("ODBC")]
    public void QueryThriftServer(string endpointVariable)
    {
        var endpoint = Environment.GetEnvironmentVariable(endpointVariable);
        Assert.That(endpoint, Is.Not.Null.And.Not.Empty, $"Set {endpointVariable} to the fixture's host:port.");
        OdbcSmokeTest.Verify(new ConnectionUrl(
            $"odbc+hive://{endpoint}/default?AuthMech=0&ThriftTransport=0&SSL=0&UseNativeQuery=1"), OdbcFactory.Instance);
    }

    [Test, Category("Manifest")]
    public void ManifestIsValid()
        => QaManifest.Validate(Path.Combine(AppContext.BaseDirectory, "qa-manifest.json"),
            "hive", "Connection", "Querying", "ODBC");
}
