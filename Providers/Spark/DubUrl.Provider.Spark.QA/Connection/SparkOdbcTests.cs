using System.Data.Odbc;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Connection;

public sealed class SparkOdbcTests
{
    private const string Url = "odbc+spark://127.0.0.1?AuthMech=0&ThriftTransport=0&SSL=0";

    [Test, Category("Live"), Category("ODBC")]
    public void OpenThriftServer()
        => OdbcSmokeTest.Verify(new ConnectionUrl(Url), OdbcFactory.Instance);

    [Test, Category("Live"), Category("ODBC")]
    public void CreateDescribeAndDropSessionCatalogTable()
        => OdbcDdlTest.Verify(new ConnectionUrl(Url), OdbcFactory.Instance, "select", "SeLeCt", "spark_catalog");
}
