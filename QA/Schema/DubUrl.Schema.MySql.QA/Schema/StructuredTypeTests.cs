using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using MySqlConnector;
using NUnit.Framework;

namespace DubUrl.Schema.MySql.QA;

[TestFixture]
[Category("MySql")]
[Category("Schema")]
public sealed class StructuredTypeTests
{
    [Test]
    public async Task JsonRoundTrips()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_SCHEMA_MYSQL")
            ?? throw new InvalidOperationException("Configure a disposable MySQL QA database.");
        using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MySqlDialect>(["mysql"]);
        var dialect = builder.Build().Get<MySqlDialect>().WithInnoDbDefaults(Version.Parse(connection.ServerVersion.Split('-')[0]));
        await StructuredTypeChecks.VerifyJsonAsync(connection, dialect, new MySqlMetadataReader(),
            new("structured_json_values", database: connection.Database));
    }
}
