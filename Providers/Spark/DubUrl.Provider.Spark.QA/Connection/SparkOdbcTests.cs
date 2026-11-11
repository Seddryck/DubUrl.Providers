using System.Data.Odbc;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Connection;

[Category("Live")]
public sealed class SparkOdbcTests : OdbcContract
{
    internal const string ThriftLocation = "odbc+spark://127.0.0.1";
    internal const string ThriftUrl = ThriftLocation
        + "?AuthMech=0&ThriftTransport=0&SSL=0&UseNativeQuery=1&EnableNativeParameterizedQuery=1&PopulateParametersForNativeQuery=1";

    protected override string AuthenticationConnectionUrl => ThriftLocation;

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
