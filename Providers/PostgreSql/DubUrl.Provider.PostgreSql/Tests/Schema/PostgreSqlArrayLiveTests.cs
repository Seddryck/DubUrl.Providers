using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Mapping;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using Npgsql;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public class PostgreSqlArrayLiveTests
{
    [Test, Explicit("Requires DUBURL_POSTGRES_ARRAY_QA_URL.")]
    public Task PostgreSqlArrayRoundTrip() => RoundTrip(Url("POSTGRES"), hypertable: false);

    [Test, Explicit("Requires DUBURL_TIMESCALE_ARRAY_QA_URL.")]
    public Task TimescaleHypertableArrayRoundTrip() => RoundTrip(Url("TIMESCALE"), hypertable: true);

    [Test, Explicit("Requires DUBURL_POSTGRES_ARRAY_QA_URL.")]
    public async Task PostgreSqlArrayDiscoveryReportsUncheckedAndUnrepresentableShapes()
    {
        var url = Url("POSTGRES");
        await using var connection = (NpgsqlConnection)url.Connect();
        await connection.OpenAsync();
        var name = "duburl_arrays_" + Guid.NewGuid().ToString("N");
        var identity = new MetadataName(name, Schema: "public");
        var reader = new PostgreSqlMetadataReader();
        var options = new DiscoveryOptions { Tables = [identity], Features = [SchemaFeature.Tables, SchemaFeature.Columns] };
        try
        {
            await Execute(connection, $"""
                CREATE TABLE public.{name} (plain int4[], matrix int4[2][2], bounded varchar(8)[],
                    collated text[] COLLATE "C", unvalidated int4[]);
                ALTER TABLE public.{name} ADD CHECK (pg_catalog.array_ndims(unvalidated)=1) NOT VALID;
                """);
            var result = await reader.ReadWithStructuredTypesAsync(connection, options, default);
            Assert.That(result.StructuredColumns, Has.Count.EqualTo(5));
            Assert.That(result.StructuredColumns.Values.Select(column => column.Coverage), Is.All.EqualTo(MetadataCoverage.Unrepresentable));
            Assert.That(result.StructuredColumns[new(identity, "plain")].Type?.Shape, Is.EqualTo(new ArrayType(new ScalarType(DbType.Int32), true)));
            Assert.That(result.StructuredColumns[new(identity, "unvalidated")].Type?.Shape, Is.EqualTo(new ArrayType(new ScalarType(DbType.Int32), true)));
            foreach (var column in new[] { "matrix", "bounded", "collated" })
                Assert.That(result.StructuredColumns[new(identity, column)].Type, Is.Null, column);

            var inspection = await new SchemaInspector().InspectAsync(connection, reader, options);
            Assert.That(inspection.IsComplete, Is.False);
            Assert.That(inspection.Diagnostics.Select(item => item.Code), Does.Contain("SDI012"));
        }
        finally
        {
            await Execute(connection, $"DROP TABLE IF EXISTS public.{name}");
        }
    }

    private static async Task RoundTrip(ConnectionUrl url, bool hypertable)
    {
        Assert.That(url.TryGetCapability<SchemaDiscoveryCapability>(out var capability), Is.True);
        Assert.That(capability!.Reader, Is.TypeOf<PostgreSqlMetadataReader>());
        await using var connection = (NpgsqlConnection)url.Connect();
        await connection.OpenAsync();
        var name = "duburl_arrays_" + Guid.NewGuid().ToString("N");
        const string arrayName = "items\"list";
        const string quoted = "\"items\"\"list\"";
        var integers = new StructuredColumnType(new ArrayType(new ScalarType(DbType.Int32), true), StructuredStorage.Native);
        var documents = new StructuredColumnType(new ArrayType(new JsonType(), true), StructuredStorage.Native);
        var schema = new DubUrl.Schema.Schema([new Table(new DatabaseObjectName(name, schema: "public"),
            [new Column("id", DbType.Int32), new Column("observed_at", DbType.DateTime2, constraints: [new NotNullableConstraint()]),
                new Column(arrayName, integers, constraints: [new NullableConstraint()]),
                new Column("docs", documents, constraints: [new NullableConstraint()])],
            [new CheckConstraint(new ColumnIdentityExpression("id"), ">", new ValueExpression(0))])]);
        var renderer = new SchemaScriptRenderer(url.Dialect);
        var table = $"public.{name}";
        try
        {
            foreach (var step in renderer.Plan(schema))
                await Execute(connection, step.CommandText);
            if (hypertable)
                await Execute(connection, $"SELECT create_hypertable('{table}', by_range('observed_at'))");

            await Execute(connection, $$"""
                INSERT INTO {{table}} VALUES
                    (1, '2026-01-01', ARRAY[1,NULL,3], ARRAY['{"items":[1,null]}'::jsonb,NULL]),
                    (2, '2026-01-01', '{}', '{}'),
                    (3, '2026-01-01', NULL, NULL);
                """);
            var wrongRank = await Assert.ThrowsAsync<PostgresException>(async () =>
                await Execute(connection, $"INSERT INTO {table} VALUES (4, '2026-01-01', ARRAY[[1,2]], NULL)"));
            Assert.That(wrongRank!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));

            await using (var command = new NpgsqlCommand($"SELECT {quoted}, docs[1]::text, cardinality(docs) FROM {table} ORDER BY id", connection))
            await using (var rows = await command.ExecuteReaderAsync())
            {
                Assert.That(await rows.ReadAsync(), Is.True);
                Assert.That(rows.GetFieldValue<int?[]>(0), Is.EqualTo(new int?[] { 1, null, 3 }));
                Assert.That(rows.GetString(1), Is.EqualTo("""{"items": [1, null]}"""));
                Assert.That(rows.GetInt32(2), Is.EqualTo(2));
                Assert.That(await rows.ReadAsync(), Is.True);
                Assert.That(rows.GetFieldValue<int[]>(0), Is.Empty);
                Assert.That(rows.GetInt32(2), Is.Zero);
                Assert.That(await rows.ReadAsync(), Is.True);
                Assert.That(rows.IsDBNull(0) && rows.IsDBNull(2), Is.True);
                Assert.That(await rows.ReadAsync(), Is.False);
            }

            var metadata = (PostgreSqlMetadataReader)capability.Reader;
            if (hypertable)
            {
                var inventory = await metadata.ReadAsync(connection, new() { Features = [SchemaFeature.Tables] });
                Assert.That(inventory.Tables.Any(item => item.Identity.Schema?.StartsWith("_timescaledb_", StringComparison.Ordinal) == true), Is.False);
            }
            var result = await new SchemaInspector().InspectAsync(connection, metadata, new()
            {
                Tables = [new(name, Schema: "public")],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Checks]
            });
            Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
            var discovered = result.Schema!.Tables.Values.Single();
            Assert.That(discovered.Columns[arrayName].StructuredType, Is.EqualTo(integers));
            Assert.That(discovered.Columns["docs"].StructuredType, Is.EqualTo(documents));
            Assert.That(discovered.Constraints.OfType<CheckConstraint>().Single().Operator, Is.EqualTo(">"));
            var script = renderer.Plan(result.Schema).Single().CommandText;
            Assert.That(script.Split("array_ndims", StringSplitOptions.None), Has.Length.EqualTo(3));
        }
        finally
        {
            await Execute(connection, $"DROP TABLE IF EXISTS {table}");
        }
    }

    private static ConnectionUrl Url(string engine)
    {
        var url = Environment.GetEnvironmentVariable($"DUBURL_{engine}_ARRAY_QA_URL")
            ?? throw new InvalidOperationException("Configure a disposable database for array QA.");
        DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);
        var registry = new ProviderCatalogBuilder().AddModule(new AttributeProviderModule([typeof(PgsqlDialect).Assembly])).Build();
        return new(url, registry);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
