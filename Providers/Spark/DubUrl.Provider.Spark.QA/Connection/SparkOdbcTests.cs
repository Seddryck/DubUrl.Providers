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
    protected override string PasswordAuthenticationConnectionUrl
    {
        get
        {
            var port = Environment.GetEnvironmentVariable("DUBURL_SPARK_LDAP_PORT");
            if (port is null) return base.PasswordAuthenticationConnectionUrl;
            var password = Environment.GetEnvironmentVariable("DUBURL_SPARK_LDAP_PASSWORD")
                ?? throw new InvalidOperationException("The Spark LDAP fixture requires its disposable login password.");
            var ca = Environment.GetEnvironmentVariable("DUBURL_SPARK_LDAP_CA")
                ?? throw new InvalidOperationException("The Spark LDAP fixture requires its CA certificate.");
            return $"odbc+spark+tcps://alice:{Uri.EscapeDataString(password)}@127.0.0.1:{port}"
                + $"?Driver=Simba%20Spark%20ODBC%20Driver&AuthMech=3&sasl=1&TrustedCerts={Uri.EscapeDataString(ca)}&UseSystemTrustStore=0&AllowSelfSignedServerCert=0&CAIssuedCertNamesMismatch=0"
                + "&UseNativeQuery=1&SocketTimeout=10";
        }
    }

    protected override string ConnectionUrl => ThriftUrl;
    protected override bool SupportsSchemaDiscovery => true;
    protected override Task VerifySchemaDiscoveryAsync(ConnectionUrl url)
        => SparkSchemaQa.VerifySharedDiscovery(url, scope => VerifySchemaDiscoveryAsync(url, scope));
    protected override string SelectFirstCustomerSql => SparkOdbcCustomerQueries.First;
    protected override string SelectCustomerByIdSql => string.Empty;
    protected override bool SupportsParameters => false;

    [Test, Category("ODBC")]
    public Task InspectExportAndRedeploySchema() => SparkSchemaQa.InspectExportAndRedeploy(NativeSchemaUrl, delta: false);

    [Test, Category("ODBC")]
    public Task StructuredRecursiveRoundTrip() => SparkStructuredQa.Recursive(NativeSchemaUrl, delta: false);

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
