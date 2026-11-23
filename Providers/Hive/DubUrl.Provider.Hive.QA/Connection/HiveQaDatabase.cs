using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

internal static class HiveQaDatabase
{
    internal const string HiveEndpoint = "HIVE_ODBC_HIVESERVER2_ENDPOINT";
    internal const string FirstCustomer = "select FullName from Customer where CustomerId=1";
    internal const string CustomerById = "select FullName from Customer where CustomerId=?";

    internal static string GetUrl(bool useNativeQuery = false)
        => $"{GetLocation()}?ThriftTransport=0&AuthMech=0&UseNativeQuery={(useNativeQuery ? 1 : 0)}";

    internal static string GetLocation()
        => $"odbc+hive://{ReadFixtureSetting(HiveEndpoint)}/default";

    internal static async Task VerifySharedDiscoveryAsync(Func<DiscoveryOptions, Task> verify)
    {
        var url = new ConnectionUrl(GetUrl(useNativeQuery: true));
        using var connection = (DbConnection)url.Open();
        var schema = "duburl_qa_" + Guid.NewGuid().ToString("N");
        QaSql.Execute(connection, $"CREATE DATABASE {schema}");
        try
        {
            QaSql.Execute(connection, $"CREATE TABLE {schema}.customer (id INT)");
            await verify(new DiscoveryOptions { Schemas = [schema] });
        }
        finally
        {
            QaSql.Execute(connection, $"DROP DATABASE {schema} CASCADE");
        }
    }

    internal static string ReadFixtureSetting(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.That(value, Is.Not.Null.And.Not.Empty, $"Set {variable} for the Hive ODBC fixture.");
        return value!;
    }
}
