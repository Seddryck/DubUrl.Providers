using System.Data;
using System.Text.Json.Nodes;
using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using Npgsql;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public sealed class PostgreSqlCompositeLiveTests
{
    [Test, Explicit("Requires DUBURL_POSTGRES_COMPOSITE_QA_CONNECTION_STRING.")]
    public async Task NamedCompositeRoundTripsWithNestedValuesAndQuotedFields()
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        var schemaName = "duburl_composite_" + Guid.NewGuid().ToString("N");
        var innerName = $"{schemaName}.\"inner\"\"type\"";
        var outerName = $"{schemaName}.outer_type";
        var table = $"{schemaName}.items";
        var inner = new StructType([new("item\"id", new ScalarType(DbType.Int32), true)]);
        var outer = new StructType([new("nested", inner, true), new("document", new JsonType(), true)]);
        var dialect = PostgreSqlCompositeTypeTests.Dialect()
            .WithCompositeType(new("inner\"type", schemaName), inner).WithCompositeType(new("outer_type", schemaName), outer);
        var type = new StructuredColumnType(outer, StructuredStorage.Native);
        var schema = new DubUrl.Schema.Schema([new Table(new DatabaseObjectName("items", schemaName),
            [new Column("id", DbType.Int32), new Column("payload", type, constraints: [new NullableConstraint()]),
                new Column("copy", type, constraints: [new NullableConstraint()])], [])]);
        var renderer = new SchemaScriptRenderer(dialect);
        try
        {
            await Execute(connection, $"CREATE SCHEMA {schemaName}");
            foreach (var statement in dialect.RenderCompositeTypeDefinitions())
                await Execute(connection, statement);
            foreach (var command in renderer.Plan(schema))
                await Execute(connection, command.CommandText);
            await Execute(connection, $$"""
                INSERT INTO {{table}} VALUES
                    (1, ROW(ROW(7)::{{innerName}}, '{"items":[1,null]}'::jsonb)::{{outerName}}, NULL),
                    (2, ROW(NULL,NULL)::{{outerName}}, NULL),
                    (3, NULL, NULL)
                """);
            await using (var command = new NpgsqlCommand($"SELECT to_jsonb(payload)::text FROM {table} ORDER BY id", connection))
            await using (var rows = await command.ExecuteReaderAsync())
            {
                foreach (var expected in new[] { """{"nested":{"item\"id":7},"document":{"items":[1,null]}}""",
                    """{"nested":null,"document":null}""" })
                {
                    Assert.That(await rows.ReadAsync(), Is.True);
                    Assert.That(JsonNode.DeepEquals(JsonNode.Parse(rows.GetString(0)), JsonNode.Parse(expected)), Is.True);
                }
                Assert.That(await rows.ReadAsync(), Is.True);
                Assert.That(rows.IsDBNull(0), Is.True);
                Assert.That(await rows.ReadAsync(), Is.False);
            }

            var options = new DiscoveryOptions { Schemas = [schemaName], Features = [SchemaFeature.Tables, SchemaFeature.Columns] };
            var reader = new PostgreSqlMetadataReader();
            var metadata = await reader.ReadWithStructuredTypesAsync(connection, options, default);
            var actualTable = metadata.Metadata.Tables.Single(item => item.Identity == new MetadataName("items", Schema: schemaName));
            Assert.That(actualTable.Columns.Single(item => item.Name == "payload").NativeType, Is.EqualTo($"\"{schemaName}\".\"outer_type\""));
            var discovered = await new SchemaInspector().InspectAsync(connection, reader, options);
            Assert.That(discovered.IsComplete, Is.True, string.Join(", ", discovered.Diagnostics.Select(item => item.Message)));
            var columns = discovered.Schema!.Tables.Values.Single().Columns;
            Assert.That(columns["payload"].StructuredType, Is.EqualTo(type));
            Assert.That(columns["copy"].StructuredType, Is.EqualTo(type));

            await Execute(connection, $"DROP TABLE {table}");
            foreach (var command in renderer.Plan(discovered.Schema))
                await Execute(connection, command.CommandText);
        }
        finally
        {
            await Execute(connection, $"DROP TABLE IF EXISTS {table}");
            await Execute(connection, $"DROP TYPE IF EXISTS {outerName}");
            await Execute(connection, $"DROP TYPE IF EXISTS {innerName}");
            await Execute(connection, $"DROP SCHEMA IF EXISTS {schemaName}");
        }
    }

    [Test, Explicit("Requires DUBURL_POSTGRES_COMPOSITE_QA_CONNECTION_STRING.")]
    public async Task DiscoveryReportsTableRowTypesAndFacetedFieldsAsIncomplete()
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        var schema = "duburl_composite_" + Guid.NewGuid().ToString("N");
        try
        {
            await Execute(connection, $"""
                CREATE SCHEMA {schema};
                CREATE TYPE {schema}.bounded AS (name varchar(8));
                CREATE TABLE {schema}.source (id int4);
                CREATE TABLE {schema}.examples (limited {schema}.bounded, row_value {schema}.source);
                """);
            var reader = new PostgreSqlMetadataReader();
            var options = new DiscoveryOptions { Schemas = [schema], Features = [SchemaFeature.Tables, SchemaFeature.Columns] };
            var result = await reader.ReadWithStructuredTypesAsync(connection, options, default);
            var table = new MetadataName("examples", Schema: schema);
            foreach (var column in new[] { "limited", "row_value" })
                Assert.That(result.StructuredColumns[new(table, column)],
                    Is.EqualTo(new StructuredColumnMetadata(null, MetadataCoverage.Unrepresentable)));
        }
        finally
        {
            await Execute(connection, $"DROP TABLE IF EXISTS {schema}.examples");
            await Execute(connection, $"DROP TABLE IF EXISTS {schema}.source");
            await Execute(connection, $"DROP TYPE IF EXISTS {schema}.bounded");
            await Execute(connection, $"DROP SCHEMA IF EXISTS {schema}");
        }
    }

    private static NpgsqlConnection Connection()
        => new(Environment.GetEnvironmentVariable("DUBURL_POSTGRES_COMPOSITE_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable database for composite QA."));

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
