using Npgsql;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Provider.PostgreSql.QA;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.PostgreSql.QA;

[TestFixture]
[Category("PostgreSql")]
[Category("Schema")]
public sealed class UniqueNullPolicyTests
{
    [TestCase(UniqueNullPolicy.NullsDistinct)]
    [TestCase(UniqueNullPolicy.NullsNotDistinct)]
    public async Task CompositeTupleEnforcementAndDiscovery(UniqueNullPolicy policy)
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["pgsql"]);
        await UniqueNullPolicyChecks.VerifyAsync(connection, builder.Build().Get<PgsqlDialect>(), new PostgreSqlMetadataReader(),
            new(policy, Schema: "public"), exception => exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation });
    }
}
