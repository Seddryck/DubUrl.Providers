using Microsoft.Data.Sqlite;
using DubUrl.Provider.Sqlite.Discovery;
using DubUrl.Provider.Sqlite.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using NUnit.Framework;

namespace DubUrl.Schema.Sqlite.QA;

[TestFixture]
[Category("Sqlite")]
[Category("Schema")]
public sealed class TemporalDefaultTests
{
    [Test]
    public async Task TemporalDefaultsEvaluateOnInsertAndAreDiscovered()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SqliteDialect>(["sqlite"]);
        await TemporalDefaultChecks.VerifyAsync(connection, builder.Build().Get<SqliteDialect>(),
            new SqliteMetadataReader(), new("typed_temporal_defaults", database: "main"), TimeSpan.Zero);
    }
}
