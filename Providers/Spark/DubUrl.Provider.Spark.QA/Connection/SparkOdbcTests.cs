using System.Data.Odbc;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Connection;

[Category("Live")]
public sealed class SparkOdbcTests : OdbcContract
{
    internal const string ThriftLocation = "odbc+spark://127.0.0.1";
    internal const string ThriftUrl = "odbc+spark+tcp://127.0.0.1?AuthMech=0&sasl=0";
    private static ConnectionUrl NativeSchemaUrl => new(ThriftUrl + "&UseNativeQuery=1");

    protected override string AuthenticationConnectionUrl => ThriftLocation;

    protected override string ConnectionUrl => ThriftUrl;
    protected override string SelectFirstCustomerSql => SparkOdbcCustomerQueries.First;
    protected override string SelectCustomerByIdSql => string.Empty;
    protected override bool SupportsParameters => false;

    [Test, Category("ODBC")]
    public void DeployProjectionViewsAndDiagnosePartialFailure() => SparkSchemaQa.ProjectionViews(NativeSchemaUrl);

    [Test, Category("ODBC")]
    public void EvaluateColumnDefaultsOnInsert() => SparkSchemaQa.Defaults(NativeSchemaUrl);

    [Test, Category("ODBC")]
    public void RenderTablesWithFacetsAndComments() => SparkSchemaQa.Tables(NativeSchemaUrl, delta: false);

    [Test, Category("ODBC")]
    public void OpenThriftServer()
        => OdbcSmokeTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance);

    [Test, Category("ODBC")]
    public void CreateDescribeAndDropSessionCatalogTable()
        => OdbcDdlTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance, "select", "SeLeCt", "spark_catalog");
}
