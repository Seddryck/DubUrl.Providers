using DubUrl.Discovery;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.MySql.QA;

public sealed partial class MySqlBaselineTests
{
    [Test]
    [Category("Schema")]
    public async Task AutoIncrement_RoundTripRetainsGenerationAndUsesTargetSessionSettings()
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();
        var name = $"duburl_identity_{Guid.NewGuid():N}";
        var quoted = QuoteIdentifier(name);
        try
        {
            QaSql.Execute(connection, $"CREATE TABLE {quoted} (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY) AUTO_INCREMENT=500");
            var options = new DiscoveryOptions
            {
                Tables = [new(name, Database: connection.Database)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.PrimaryKeys, SchemaFeature.Generation],
            };
            var result = await new SchemaInspector().InspectAsync(connection, new MySqlMetadataReader(), options);
            Assert.That(result.Diagnostics, Is.Empty);
            var table = result.Schema!.Tables.Values.Single();
            Assert.That(table.Columns["id"].Generation, Is.TypeOf<ProviderDefaultIdentityColumnGeneration>());

            var sql = new SchemaScriptRenderer(CreateDialect()).Render(result.Schema);
            Assert.That(sql, Does.Contain("AUTO_INCREMENT").And.Not.Contain("500"));
            QaSql.Execute(connection, $"DROP TABLE {quoted}");
            QaSql.Execute(connection, sql);
            QaSql.Execute(connection, "SET SESSION auto_increment_increment = 3, auto_increment_offset = 2");
            QaSql.Execute(connection, $"INSERT INTO {quoted} () VALUES ()");
            Assert.That(Convert.ToInt64(QaSql.Rows(connection, $"SELECT id FROM {quoted}").Single()[0]), Is.EqualTo(2));
        }
        finally
        {
            QaSql.Execute(connection, $"DROP TABLE IF EXISTS {quoted}");
        }
    }
}
