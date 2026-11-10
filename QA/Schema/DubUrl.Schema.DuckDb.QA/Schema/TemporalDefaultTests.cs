using DuckDB.NET.Data;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using NUnit.Framework;

namespace DubUrl.Schema.DuckDb.QA;

[TestFixture]
[Category("DuckDb")]
[Category("Schema")]
public sealed class TemporalDefaultTests
{
    [Test]
    public async Task TemporalDefaultsEvaluateOnInsertAndAreDiscovered()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SET TimeZone = 'Asia/Kolkata'";
            command.ExecuteNonQuery();
        }
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        await TemporalDefaultChecks.VerifyAsync(connection, builder.Build().Get<DuckDbDialect>(),
            new DuckDbMetadataReader(), new("typed_temporal_defaults", schema: "main"), TimeSpan.FromMinutes(330));
    }
}
