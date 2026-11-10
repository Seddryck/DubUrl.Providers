using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using MySqlConnector;
using NUnit.Framework;

namespace DubUrl.Provider.MySql.Tests.Schema;

public class MariaDbStructuredTypeLiveTests
{
    [Test]
    [Explicit("Requires a disposable MariaDB 11.7.1+ database in DUBURL_MARIADB_STRUCTURED_QA_CONNECTION_STRING.")]
    public async Task JsonTextAndVectorRoundTrip()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_MARIADB_STRUCTURED_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable MariaDB database.");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        var name = "duburl_types_" + Guid.NewGuid().ToString("N");
        var identity = new DatabaseObjectName(name, database: connection.Database);
        const string document = "payload`doc";
        var json = new StructuredColumnType(new JsonType(), StructuredStorage.JsonText);
        var vector = new StructuredColumnType(new VectorType(DbType.Single, 3), StructuredStorage.Native);
        var schema = new DubUrl.Schema.Schema([new Table(identity,
            [new Column("id", DbType.Int32), new Column(document, json, constraints: [new NullableConstraint()]),
                new Column("embedding", vector, constraints: [new NullableConstraint()])], [])]);
        var renderer = new SchemaScriptRenderer(MariaDbStructuredTypeTests.Dialect().WithMariaDbDefaults(new(11, 7, 1)));
        var table = $"`{name}`";
        try
        {
            foreach (var step in renderer.Plan(schema))
                await ExecuteAsync(connection, step.CommandText);
            await ExecuteAsync(connection, $"ALTER TABLE {table} ADD plain LONGTEXT COLLATE utf8mb4_bin, ADD CONSTRAINT positive CHECK (id > 0)");
            await ExecuteAsync(connection, $$"""
                INSERT INTO {{table}} VALUES (1, '{"items":[1,null,true]}', VEC_FromText('[1,2,3]'), 'plain text'),
                    (2, NULL, NULL, NULL)
                """);
            var invalidJson = await Assert.ThrowsAsync<MySqlException>(async () =>
                await ExecuteAsync(connection, $"INSERT INTO {table} (id,`payload``doc`) VALUES (3,'invalid')"));
            Assert.That(invalidJson!.Number, Is.EqualTo(4025));

            await using (var command = new MySqlCommand($"SELECT `payload``doc`, VEC_ToText(embedding) FROM {table} ORDER BY id", connection))
            await using (var data = await command.ExecuteReaderAsync())
            {
                Assert.That(await data.ReadAsync(), Is.True);
                Assert.That(data.GetString(0), Is.EqualTo("""{"items":[1,null,true]}"""));
                Assert.That(System.Text.Json.JsonSerializer.Deserialize<float[]>(data.GetString(1)), Is.EqualTo(new[] { 1f, 2f, 3f }));
                Assert.That(await data.ReadAsync(), Is.True);
                Assert.That(data.IsDBNull(0) && data.IsDBNull(1), Is.True);
                Assert.That(await data.ReadAsync(), Is.False);
            }

            var options = new DiscoveryOptions
            {
                Tables = [new(name, Database: connection.Database)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Checks]
            };
            var result = await new SchemaInspector().InspectAsync(connection, new MariaDbMetadataReader(), options);
            Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
            var columns = result.Schema!.Tables.Values.Single().Columns;
            Assert.That(columns[document].StructuredType, Is.EqualTo(json));
            Assert.That(columns["embedding"].StructuredType, Is.EqualTo(vector));
            Assert.That(columns["plain"].StructuredType, Is.Null);
            Assert.That(renderer.Plan(result.Schema).Single().CommandText, Does.Contain("LONGTEXT").And.Contain("CHECK"));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
