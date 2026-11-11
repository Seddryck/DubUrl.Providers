using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

internal static class HiveQaDatabase
{
    internal const string HiveEndpoint = "HIVE_ODBC_HIVESERVER2_ENDPOINT";
    internal const string SparkEndpoint = "HIVE_ODBC_SPARK_ENDPOINT";
    internal const string FirstCustomer = "select FullName from Customer where CustomerId=1";
    internal const string CustomerById = "select FullName from Customer where CustomerId=?";

    internal static string GetUrl(string endpointVariable = HiveEndpoint, bool useNativeQuery = false)
    {
        var endpoint = Environment.GetEnvironmentVariable(endpointVariable);
        Assert.That(endpoint, Is.Not.Null.And.Not.Empty, $"Set {endpointVariable} to the fixture's host:port.");
        return $"odbc+hive://{endpoint}/default?ThriftTransport=0&UseNativeQuery={(useNativeQuery ? 1 : 0)}";
    }
}
