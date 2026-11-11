using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

[Category("Live"), Category("ODBC")]
public sealed class HiveTransportTests
{
    [Test]
    public void OpenHiveServer2OverTcp()
    {
        var endpoint = ReadFixtureSetting(HiveQaDatabase.HiveEndpoint);
        var url = new ConnectionUrl($"odbc+hive+tcp://{endpoint}/schema/default?sasl=0&AuthMech=0&UseNativeQuery=1");
        OdbcSmokeTest.Verify(url, OdbcFactory.Instance);
    }

    [TestCase("http", "HIVE_ODBC_HTTP_ENDPOINT")]
    [TestCase("https", "HIVE_ODBC_HTTPS_ENDPOINT")]
    [TestCase("tcps", "HIVE_ODBC_TCPS_ENDPOINT")]
    public void PinnedDriverCannotOpenHiveServer2Transport(string transport, string endpointVariable)
    {
        var endpoint = ReadFixtureSetting(endpointVariable);
        var url = $"odbc+hive+{transport}://{endpoint}/schema/default?AuthMech=0&UseNativeQuery=1";
        if (transport == "tcps")
            url += "&sasl=0";
        if (transport != "http")
            url += $"&TrustedCerts={Uri.EscapeDataString(ReadFixtureSetting("HIVE_ODBC_TLS_CERT"))}";

        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        using var connection = new ConnectionUrl(url).Connect();

        // The pinned driver sends raw binary Thrift for all three settings.
        // The runner independently checks that HiveServer2 answers on the requested transport.
        var error = Assert.Throws<OdbcException>(() => connection.Open());
        Assert.That(error!.Errors.Cast<OdbcError>().Select(item => item.Message),
            Has.Some.Contains("No more data to read"));
    }

    private static string ReadFixtureSetting(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.That(value, Is.Not.Null.And.Not.Empty, $"Set {variable} for the HiveServer2 fixture.");
        return value!;
    }
}
