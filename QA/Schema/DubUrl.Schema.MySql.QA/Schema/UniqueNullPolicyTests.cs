using MySqlConnector;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.MySql.QA;

[TestFixture]
[Category("MySql")]
[Category("Schema")]
public sealed class UniqueNullPolicyTests
{
    [Test]
    public async Task CompositeNullsDistinctEnforcesAndDiscoversInOrder()
    {
        using var connection = new MySqlConnection(Environment.GetEnvironmentVariable("DUBURL_SCHEMA_MYSQL")
            ?? throw new InvalidOperationException("Configure a disposable MySQL QA database."));
        await connection.OpenAsync();
        await UniqueNullPolicyChecks.VerifyAsync(connection, Dialect(), new MySqlMetadataReader(),
            new(UniqueNullPolicy.NullsDistinct, Database: connection.Database),
            exception => exception is MySqlException { Number: 1062 });
    }

    [Test]
    public void NullableNullsNotDistinctIsRejected()
        => UniqueNullPolicyChecks.RejectNullablePolicy(Dialect(), UniqueNullPolicy.NullsNotDistinct);

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MySqlDialect>(["mysql"]);
        return builder.Build().Get<MySqlDialect>();
    }
}
