using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using DubUrl.Discovery;
using DubUrl.Provider.Snowflake.Discovery;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using Snowflake.Data.Client;

namespace DubUrl.Provider.Snowflake.Tests.Schema;

public class SnowflakeStructuredTypeLiveTests
{
    [Test]
    [Explicit("Requires a disposable schema in DUBURL_SNOWFLAKE_QA_CONNECTION_STRING.")]
    public async Task AllFiveShapesRoundTrip()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_SNOWFLAKE_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable Snowflake schema.");
        await using var connection = new SnowflakeDbConnection { ConnectionString = connectionString };
        await connection.OpenAsync();

        var name = "DUBURL_TYPES_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var identity = new DatabaseObjectName(name);
        var integer = new ScalarType(DbType.Int32);
        var text = new ScalarType(DbType.String);
        var types = new Dictionary<string, StructuredColumnType>
        {
            ["DOC"] = new(new JsonType(), StructuredStorage.Json),
            ["ITEMS"] = Native(new ArrayType(integer, false)),
            ["ATTRS"] = Native(new MapType(text, new ArrayType(new ScalarType(DbType.Boolean), true), false)),
            ["DETAIL"] = Native(new StructType([new("item.id", integer, false), new("note", text, true)])),
            ["EMBEDDING"] = Native(new VectorType(DbType.Int32, 3))
        };
        var columns = types.Select(item => new Column(item.Key, item.Value, constraints: [new NullableConstraint()]));
        var schema = new DubUrl.Schema.Schema([new Table(identity, [new Column("ID", DbType.Int32), .. columns], [])]);
        var renderer = new SchemaScriptRenderer(SnowflakeStructuredTypeTests.Dialect());

        foreach (var command in renderer.Plan(schema))
            await ExecuteAsync(connection, command.CommandText);
        try
        {
            await ExecuteAsync(connection, $$"""
                INSERT INTO {{name}} SELECT 1, PARSE_JSON('{"items":[1,null,{"ok":true}]}'),
                    [1, 2]::ARRAY(NUMBER(10,0) NOT NULL), {'a':[true,false]}::MAP(VARCHAR, ARRAY(BOOLEAN) NOT NULL),
                    {'item.id':7,'note':'ready'}::OBJECT("item.id" NUMBER(10,0) NOT NULL, "note" VARCHAR),
                    [1,2,3]::VECTOR(INT,3)
                """);
            await ExecuteAsync(connection, $$"""
                INSERT INTO {{name}} SELECT 2, PARSE_JSON('{}'), []::ARRAY(NUMBER(10,0) NOT NULL),
                    {}::MAP(VARCHAR, ARRAY(BOOLEAN) NOT NULL), NULL, NULL
                """);
            await ExecuteAsync(connection, $"INSERT INTO {name} VALUES (3, NULL, NULL, NULL, NULL, NULL)");

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT TO_JSON(DOC),TO_JSON(ITEMS::VARIANT),TO_JSON(ATTRS::VARIANT),
                        TO_JSON(DETAIL::VARIANT),TO_JSON(TO_ARRAY(EMBEDDING)::VARIANT)
                    FROM {name} ORDER BY ID
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.That(await reader.ReadAsync(), Is.True);
                AssertJson(reader, ["""{"items":[1,null,{"ok":true}]}""", "[1,2]", """{"a":[true,false]}""",
                    """{"item.id":7,"note":"ready"}""", "[1,2,3]"]);
                Assert.That(await reader.ReadAsync(), Is.True);
                AssertJson(reader, ["{}", "[]", "{}", null, null]);
                Assert.That(await reader.ReadAsync(), Is.True);
                AssertJson(reader, [null, null, null, null, null]);
                Assert.That(await reader.ReadAsync(), Is.False);
            }

            var inspected = await new SchemaInspector().InspectAsync(connection, new SnowflakeMetadataReader(), new()
            {
                Tables = [new(name)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns]
            });
            Assert.That(inspected.IsComplete, Is.True, string.Join(", ", inspected.Diagnostics.Select(item => item.Message)));
            var discovered = inspected.Schema!.Tables.Values.Single().Columns;
            foreach (var (column, type) in types)
                Assert.That(discovered[column].StructuredType, Is.EqualTo(type), column);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE {name}");
        }
    }

    private static StructuredColumnType Native(StructuredType shape) => new(shape, StructuredStorage.Native);

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertJson(DbDataReader reader, string?[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] is { } json)
                Assert.That(JsonNode.DeepEquals(JsonNode.Parse(reader.GetString(i)), JsonNode.Parse(json)), Is.True, $"Column {i}");
            else
                Assert.That(reader.IsDBNull(i), Is.True, $"Column {i}");
        }
    }
}
