using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

internal static class HiveQaDatabase
{
    internal const string HiveEndpoint = "HIVE_ODBC_HIVESERVER2_ENDPOINT";
    internal const string SparkEndpoint = "HIVE_ODBC_SPARK_ENDPOINT";
    internal const string FirstCustomer = "select FullName from Customer where CustomerId=1";
    internal const string CustomerById = "select FullName from Customer where CustomerId=?";

    internal static string GetUrl(string endpointVariable = HiveEndpoint, bool useNativeQuery = false)
        => $"{GetLocation(endpointVariable)}?ThriftTransport=0&AuthMech=0&UseNativeQuery={(useNativeQuery ? 1 : 0)}";

    internal static string GetLocation(string endpointVariable = HiveEndpoint)
        => $"odbc+hive://{ReadFixtureSetting(endpointVariable)}/default";

    internal static string ReadFixtureSetting(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.That(value, Is.Not.Null.And.Not.Empty, $"Set {variable} for the Hive ODBC fixture.");
        return value!;
    }
}
