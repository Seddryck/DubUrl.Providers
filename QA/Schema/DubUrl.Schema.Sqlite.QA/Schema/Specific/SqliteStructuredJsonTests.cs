using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.Sqlite.Discovery;
using DubUrl.Provider.Sqlite.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace DubUrl.Provider.Sqlite.QA;

public class SqliteStructuredJsonTests
{
    [TestCase(StructuredStorage.JsonText)]
    [TestCase(StructuredStorage.Binary)]
    public async Task ValidatedJsonRoundTripsAndRejectsInvalidStorage(StructuredStorage storage)
    {
        SqliteTestDatabase.EnsureInitialized();
        await using var connection = new SqliteConnection(SqliteTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        TestContext.WriteLine($"SQLite {connection.ServerVersion}");
        await Execute(connection, """ATTACH DATABASE ':memory:' AS "docs""db" """);
        var renderer = Renderer(Version.Parse(connection.ServerVersion));
        var type = new StructuredColumnType(new JsonType(), storage);
        var sql = renderer.Plan(Model("docs\"db", type)).Single().CommandText;
        await Execute(connection, sql);

        const string table = "\"docs\"\"db\".\"items\"";
        const string column = "\"payload\"\"json\"";
        var binary = storage == StructuredStorage.Binary;
        var codec = binary ? "jsonb" : "json";
        await Execute(connection, $$"""
            INSERT INTO {{table}} (id,{{column}}) VALUES
                (1,{{codec}}('{"items":[1,null,{"ok":true}]}')),
                (2,{{codec}}('{}')),
                (3,{{codec}}('[]')),
                (4,{{codec}}('null')),
                (5,NULL);
            """);

        var invalidValues = binary ? new[] { "'[]'", "X'1BFF'" } : new[] { "'invalid'", "'{bad:1}'", "CAST('[]' AS BLOB)" };
        foreach (var value in invalidValues)
        {
            var error = await Assert.ThrowsAsync<SqliteException>(async () =>
                await Execute(connection, $"INSERT INTO {table} (id,{column}) VALUES (6,{value})"));
            Assert.That(error!.SqliteExtendedErrorCode, Is.EqualTo(275));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT typeof({column}),json({column}) FROM {table} ORDER BY id";
            await using var data = await command.ExecuteReaderAsync();
            foreach (var expected in new[] { """{"items":[1,null,{"ok":true}]}""", "{}", "[]", "null" })
            {
                Assert.That(await data.ReadAsync(), Is.True);
                Assert.That(data.GetString(0), Is.EqualTo(binary ? "blob" : "text"));
                Assert.That(data.GetString(1), Is.EqualTo(expected));
            }
            Assert.That(await data.ReadAsync(), Is.True);
            Assert.That(data.IsDBNull(1), Is.True);
            Assert.That(await data.ReadAsync(), Is.False);
        }

        var inspector = new SchemaInspector();
        var options = new DiscoveryOptions
        {
            Databases = ["docs\"db"],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Checks]
        };
        var result = await inspector.InspectAsync(connection, new SqliteMetadataReader(), options);
        Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
        var discovered = result.Schema!.Tables.Values.Single();
        Assert.That(discovered.Columns["payload\"json"].StructuredType, Is.EqualTo(type));
        Assert.That(discovered.Constraints.OfType<CheckConstraint>().Single().Operator, Is.EqualTo(">"));
        var rerendered = renderer.Plan(result.Schema).Single().CommandText;
        Assert.That(rerendered.Split("json_valid", StringSplitOptions.None), Has.Length.EqualTo(2));
        await Execute(connection, $"DROP TABLE {table}");
        await Execute(connection, rerendered);
    }

    [Test]
    public async Task DiscoveryDistinguishesOrdinaryStorageAndDiagnosesBareJsonDeclarations()
    {
        SqliteTestDatabase.EnsureInitialized();
        await using var connection = new SqliteConnection(SqliteTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        await Execute(connection, """
            CREATE TABLE items (txt TEXT, bin BLOB, weak TEXT CHECK (json_valid(weak)));
            CREATE TABLE ambiguous (tag JSONB);
            """);
        var inspector = new SchemaInspector();
        var reader = new SqliteMetadataReader();
        var options = new DiscoveryOptions
        {
            Features = [SchemaFeature.Tables, SchemaFeature.Columns],
            Tables = [new("items", Database: "main")]
        };
        var result = await inspector.InspectAsync(connection, reader, options);
        Assert.That(result.IsComplete, Is.True);
        var columns = result.Schema!.Tables.Values.Single().Columns;
        Assert.That(columns.Keys, Is.EquivalentTo(new[] { "txt", "bin", "weak" }));
        Assert.That(columns.Values.Select(column => column.StructuredType), Is.All.Null);

        var ambiguous = await inspector.InspectAsync(connection, reader, options with { Tables = [new("ambiguous", Database: "main")] });
        Assert.That(ambiguous.IsComplete, Is.False);
        Assert.That(ambiguous.Diagnostics.Any(item => item.Code == "SDI012" && item.Message.Contains("tag", StringComparison.Ordinal)), Is.True);
    }

    private static DubUrl.Schema.Schema Model(string database, StructuredColumnType type)
        => new([new Table(new DatabaseObjectName("items", database: database),
            [new Column("id", DbType.Int64), new Column("payload\"json", type, constraints: [new NullableConstraint()])],
            [new CheckConstraint(new ColumnIdentityExpression("id"), ">", new ValueExpression(0))])]);

    private static SchemaScriptRenderer Renderer(Version version)
    {
        var registry = new DialectRegistryBuilder();
        registry.AddDialect<SqliteDialect>(["sqlite"]);
        var dialect = registry.Build().Get<SqliteDialect>();
        return new(dialect.WithServerVersion(version));
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
