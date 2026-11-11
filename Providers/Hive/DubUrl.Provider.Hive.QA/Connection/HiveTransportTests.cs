using System.Data.Odbc;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

[Category("Live"), Category("ODBC")]
public sealed class HiveTransportTests
{
    [TestCase("tcp", HiveQaDatabase.HiveEndpoint, TestName = "OpenHiveServer2OverTcp")]
    [TestCase("tcps", "HIVE_ODBC_TCPS_ENDPOINT", TestName = "OpenHiveServer2OverTcps")]
    public void QueryBinaryTransport(string transport, string endpointVariable)
    {
        var endpoint = HiveQaDatabase.ReadFixtureSetting(endpointVariable);
        var url = $"odbc+hive+{transport}://{endpoint}/schema/default?sasl=0&AuthMech=0&UseNativeQuery=1";
        OdbcSmokeTest.Verify(new ConnectionUrl(WithTlsCertificate(url, transport == "tcps")), OdbcFactory.Instance);
    }

    [TestCase("http", "HIVE_ODBC_HTTP_ENDPOINT", TestName = "OpenHiveServer2OverHttp")]
    [TestCase("https", "HIVE_ODBC_HTTPS_ENDPOINT", TestName = "OpenHiveServer2OverHttps")]
    public void QueryHttpTransport(string transport, string endpointVariable)
    {
        var endpoint = HiveQaDatabase.ReadFixtureSetting(endpointVariable);
        var password = Uri.EscapeDataString(HiveQaDatabase.ReadFixtureSetting("HIVE_ODBC_HTTP_PASSWORD"));
        var url = $"odbc+hive+{transport}://hive:{password}@{endpoint}/schema/default?AuthMech=3&UseNativeQuery=1";
        OdbcSmokeTest.Verify(new ConnectionUrl(WithTlsCertificate(url, transport == "https")), OdbcFactory.Instance);
    }

    private static string WithTlsCertificate(string url, bool tls)
        => tls ? $"{url}&TrustedCerts={Uri.EscapeDataString(HiveQaDatabase.ReadFixtureSetting("HIVE_ODBC_TLS_CERT"))}" : url;
}
