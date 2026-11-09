using Microsoft.Data.Sqlite;
using DubUrl.Provider.Sqlite.Discovery;
using DubUrl.Provider.Sqlite.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Sqlite.QA;

[TestFixture]
[Category("Sqlite")]
[Category("Schema")]
public sealed class UniqueNullPolicyTests
{
    [Test]
    public async Task CompositeNullsDistinctEnforcesAndDiscoversInOrder()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await UniqueNullPolicyChecks.VerifyAsync(connection, Dialect(), new SqliteMetadataReader(),
            new(UniqueNullPolicy.NullsDistinct, Database: "main", CatalogName: table => $"sqlite_autoindex_{table}_1"),
            exception => exception is SqliteException { SqliteExtendedErrorCode: 2067 });
    }

    [Test]
    public void NullableNullsNotDistinctIsRejected()
        => UniqueNullPolicyChecks.RejectNullablePolicy(Dialect(), UniqueNullPolicy.NullsNotDistinct);

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SqliteDialect>(["sqlite"]);
        return builder.Build().Get<SqliteDialect>();
    }
}
