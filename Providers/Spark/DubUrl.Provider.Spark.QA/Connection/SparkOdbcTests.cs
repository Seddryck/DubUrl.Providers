using System.Data.Odbc;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Connection;

[Category("Live")]
public sealed class SparkOdbcTests : OdbcContract
{
    internal const string ThriftUrl = "odbc+spark://127.0.0.1?AuthMech=0&ThriftTransport=0&SSL=0";

    protected override string ConnectionUrl => ThriftUrl;
    protected override string SelectFirstCustomerSql => SparkOdbcCustomerQueries.First;
    protected override string SelectCustomerByIdSql => SparkOdbcCustomerQueries.ById;

    [Test, Category("ODBC")]
    public void OpenThriftServer()
        => OdbcSmokeTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance);

    [Test, Category("ODBC")]
    public void CreateDescribeAndDropSessionCatalogTable()
        => OdbcDdlTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance, "select", "SeLeCt", "spark_catalog");
}
