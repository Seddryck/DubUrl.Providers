using Microsoft.Data.SqlClient;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using NUnit.Framework;

namespace DubUrl.Schema.SqlServer.QA;

[TestFixture]
[Category("SqlServer")]
[Category("Schema")]
public sealed class TemporalDefaultTests
{
    [Test]
    public async Task TemporalDefaultsEvaluateOnInsertAndAreDiscovered()
    {
        using var connection = new SqlConnection(Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable SQL Server QA database."));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DATEPART(TZOFFSET, SYSDATETIMEOFFSET())";
        var offset = (int)command.ExecuteScalar()!;
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["sqlserver"]);
        await TemporalDefaultChecks.VerifyAsync(connection, builder.Build().Get<TSqlDialect>(),
            new SqlServerMetadataReader(), new("typed_temporal_defaults", schema: "dbo"), TimeSpan.FromMinutes(offset));
    }
}
