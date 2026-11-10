using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.QA;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using Npgsql;
using NUnit.Framework;

namespace DubUrl.Schema.PostgreSql.QA;

[TestFixture]
[Category("PostgreSql")]
[Category("Schema")]
public sealed class StructuredTypeTests
{
    [Test]
    public async Task JsonRoundTrips()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["postgresql"]);
        await StructuredTypeChecks.VerifyJsonAsync(connection, builder.Build().Get<PgsqlDialect>(),
            new PostgreSqlMetadataReader(), new("structured_json_values", "public"));
    }
}
