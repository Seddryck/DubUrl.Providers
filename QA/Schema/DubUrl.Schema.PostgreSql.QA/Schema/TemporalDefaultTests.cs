using Npgsql;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.QA;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using NUnit.Framework;

namespace DubUrl.Schema.PostgreSql.QA;

[TestFixture]
[Category("PostgreSql")]
[Category("Schema")]
public sealed class TemporalDefaultTests
{
    [Test]
    public async Task TemporalDefaultsEvaluateOnInsertAndAreDiscovered()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SET TIME ZONE 'Asia/Kolkata'";
        command.ExecuteNonQuery();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["postgresql"]);
        await TemporalDefaultChecks.VerifyAsync(connection, builder.Build().Get<PgsqlDialect>(),
            new PostgreSqlMetadataReader(), new("typed_temporal_defaults", schema: "public"), TimeSpan.FromMinutes(330));
    }
}
