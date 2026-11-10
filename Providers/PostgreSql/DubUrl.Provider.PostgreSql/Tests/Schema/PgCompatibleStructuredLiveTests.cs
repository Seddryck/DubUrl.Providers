using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using Npgsql;
using NUnit.Framework;
using static DubUrl.Provider.PostgreSql.Tests.Schema.PgCompatibleStructuredTypeTests;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public class PgCompatibleStructuredLiveTests
{
    [Test, Explicit("Requires DUBURL_COCKROACH_STRUCTURED_QA_CONNECTION_STRING.")]
    public async Task CockroachJsonArraysAndVectorsRoundTrip()
    {
        var dialect = Dialect<CockRoachDialect>().WithServerVersion(new(24, 2));
        await RoundTrip("COCKROACH", "public", dialect, new CockroachMetadataReader(),
            [("doc", new(new JsonType(), StructuredStorage.Json)), ("items", Native(new ArrayType(Integer, true))),
                ("vec", Native(new VectorType(DbType.Single, 3)))],
            """'{"x":[1,null]}'::JSONB, ARRAY[1,NULL,2]::INT4[], '[1,2,3]'::VECTOR""",
            "(doc->'x'->>0)::INT4, items[3], (vec::TEXT = '[1,2,3]')::INT4", [1, 2, 1],
            "ADD COLUMN unsupported VECTOR");
    }

    [Test, Explicit("Requires DUBURL_CRATE_STRUCTURED_QA_CONNECTION_STRING.")]
    public async Task CrateStrictObjectsNestedArraysAndVectorsRoundTrip()
    {
        var dialect = Dialect<CrateDbDialect>().WithServerVersion(new(5, 10));
        var record = new StructType([new("value", Integer, true), new("nested",
            new StructType([new("numbers", new ArrayType(Integer, true), true)]), true)]);
        await RoundTrip("CRATE", "doc", dialect, new CrateDbMetadataReader(),
            [("obj", Native(record)), ("items", Native(new ArrayType(new ArrayType(Integer, true), true))),
                ("objects", Native(new ArrayType(record, true))), ("vec", Native(new VectorType(DbType.Single, 3)))],
            "{value=4, nested={numbers=[1,null,2]}}, [[1],null,[1,2]], [{value=5},null], [1,2,3]",
            "obj['value'], array_length(items, 1), objects[1]['value'], CASE WHEN vec = [1,2,3] THEN 1 ELSE 0 END", [4, 3, 5, 1],
            "ADD COLUMN unsupported OBJECT(DYNAMIC)", refresh: true);
    }

    [Test, Explicit("Requires DUBURL_QUEST_STRUCTURED_QA_CONNECTION_STRING.")]
    public async Task QuestNullableDoubleArraysRoundTrip()
    {
        var dialect = Dialect<QuestDbDialect>().WithServerVersion(new(9, 0));
        await RoundTrip("QUEST", null, dialect, new QuestDbMetadataReader(),
            [("items", Native(new ArrayType(DoubleLeaf, true)))], "ARRAY[1.0, NULL, 3.0]",
            "CAST(items[3] AS INT)", [3], "ADD COLUMN unsupported DOUBLE[][]");
    }

    private static async Task RoundTrip(string engine, string? schemaName, IDialect dialect, IStructuredColumnMetadataReader reader,
        (string Name, StructuredColumnType Type)[] types, string values, string projection, int[] expected, string unsupported,
        bool refresh = false)
    {
        var connectionString = Environment.GetEnvironmentVariable($"DUBURL_{engine}_STRUCTURED_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable engine for structured type QA.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var name = "duburl_types_" + Guid.NewGuid().ToString("N");
        var qualified = schemaName is null ? name : $"{schemaName}.{name}";
        var columns = types.Select(item => new Column(item.Name, item.Type, constraints: [new NullableConstraint()]));
        var model = new DubUrl.Schema.Schema([new Table(new DatabaseObjectName(name, schema: schemaName),
            [new Column("id", DbType.Int32), .. columns], [])]);
        var renderer = new SchemaScriptRenderer(dialect);
        try
        {
            foreach (var step in renderer.Plan(model))
                await Execute(connection, step.CommandText);
            var columnNames = string.Join(", ", types.Select(item => item.Name));
            await Execute(connection, $"INSERT INTO {qualified} (id, {columnNames}) VALUES (1, {values})");
            await Execute(connection, $"INSERT INTO {qualified} (id) VALUES (2)");
            if (refresh)
                await Execute(connection, $"REFRESH TABLE {qualified}");

            await using (var command = new NpgsqlCommand($"SELECT {projection} FROM {qualified} WHERE id = 1", connection))
            await using (var rows = await command.ExecuteReaderAsync())
            {
                Assert.That(await rows.ReadAsync(), Is.True);
                for (var index = 0; index < expected.Length; index++)
                    Assert.That(Convert.ToInt32(rows.GetValue(index)), Is.EqualTo(expected[index]));
            }
            await using (var command = new NpgsqlCommand($"SELECT {columnNames} FROM {qualified} WHERE id = 2", connection))
            await using (var rows = await command.ExecuteReaderAsync())
            {
                Assert.That(await rows.ReadAsync(), Is.True);
                for (var index = 0; index < types.Length; index++)
                    Assert.That(rows.IsDBNull(index), Is.True);
            }

            var options = new DiscoveryOptions
            {
                Tables = [new(name, Schema: schemaName)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns]
            };
            var inspector = new SchemaInspector();
            var result = await inspector.InspectAsync(connection, reader, options);
            Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
            var found = result.Schema!.Tables.Values.Single().Columns;
            Assert.That(found.Count, Is.EqualTo(types.Length + 1));
            foreach (var (column, type) in types)
                Assert.That(found[column].StructuredType, Is.EqualTo(type), column);

            await Execute(connection, $"ALTER TABLE {qualified} {unsupported}");
            var incomplete = await inspector.InspectAsync(connection, reader, options);
            Assert.That(incomplete.Diagnostics.Any(item => item.Code == "SDI012" && item.Message.Contains("unsupported", StringComparison.Ordinal)), Is.True);
        }
        finally
        {
            await Execute(connection, $"DROP TABLE IF EXISTS {qualified}");
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
