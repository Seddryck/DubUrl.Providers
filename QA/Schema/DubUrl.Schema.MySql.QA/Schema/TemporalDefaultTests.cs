using MySqlConnector;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using NUnit.Framework;

namespace DubUrl.Schema.MySql.QA;

[TestFixture]
[Category("MySql")]
[Category("Schema")]
public sealed class TemporalDefaultTests
{
    [Test]
    public async Task TemporalDefaultsEvaluateOnInsertAndAreDiscovered()
    {
        using var connection = new MySqlConnection(Environment.GetEnvironmentVariable("DUBURL_SCHEMA_MYSQL")
            ?? throw new InvalidOperationException("Configure a disposable MySQL QA database."));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SET time_zone = '+05:30'";
        command.ExecuteNonQuery();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MySqlDialect>(["mysql"]);
        await TemporalDefaultChecks.VerifyAsync(connection, builder.Build().Get<MySqlDialect>().WithInnoDbDefaults(Version.Parse(connection.ServerVersion.Split('-')[0])),
            new MySqlMetadataReader(), new("typed_temporal_defaults", database: connection.Database), TimeSpan.FromMinutes(330), System.Data.DbType.DateTime2);
    }
}
