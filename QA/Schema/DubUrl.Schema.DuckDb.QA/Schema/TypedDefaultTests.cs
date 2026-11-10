using System.Data;
using DuckDB.NET.Data;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.DuckDb.QA;

[TestFixture]
[Category("DuckDb")]
[Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task CompatibilityUsersAndInsertSessionContextAreDiscovered()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var identity = new DatabaseObjectName("typed_context", "main");
        var defaults = new[]
        {
            ColumnDefaultExpression.CurrentUser, ColumnDefaultExpression.SessionUser,
            ColumnDefaultExpression.CurrentSchema, ColumnDefaultExpression.CurrentDatabase
        };
        var columns = defaults.Select((expression, i) => new VarLengthColumn($"v{i}", DbType.AnsiString, 128,
            defaultExpression: expression)).Cast<Column>().ToArray();
        var uuid = ColumnDefaultExpression.NewUuid(UuidRepresentation.Native);
        var table = Create(connection, Dialect(), identity, [.. columns, new Column("uuid_value", DbType.Guid, defaultExpression: uuid)]);
        Execute(connection, "CREATE SCHEMA other");
        Execute(connection, "SET schema = 'other'");
        Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");

        Assert.That(Scalar(connection, $"SELECT v0 FROM {table}"), Is.EqualTo("duckdb"));
        Assert.That(Scalar(connection, $"SELECT v1 FROM {table}"), Is.EqualTo("duckdb"));
        Assert.That(Scalar(connection, $"SELECT v2 FROM {table}"), Is.EqualTo("other"));
        Assert.That(Scalar(connection, $"SELECT v3 FROM {table}"), Is.EqualTo(Scalar(connection, "SELECT current_database()")));
        Assert.That(Scalar(connection, $"SELECT uuid_value FROM {table}"), Is.TypeOf<Guid>());
        var discovered = await DiscoverAsync(connection, new DuckDbMetadataReader(), identity);
        Assert.That(discovered.Columns["uuid_value"].DefaultExpression, Is.EqualTo(uuid));
        for (var i = 0; i < defaults.Length; i++)
            Assert.That(discovered.Columns[$"v{i}"].DefaultExpression, Is.EqualTo(defaults[i]));
    }

    [Test]
    public async Task QuotedExternalSequenceIsSharedAndSurvivesTableReplacement()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var identity = new DatabaseObjectName("external_sequence_one", "main");
        var other = new DatabaseObjectName("external_sequence_two", "main");
        var sequence = new DatabaseObjectName("dots. space and ' quote", "main");
        Execute(connection, """CREATE SEQUENCE main."dots. space and ' quote" START 13 INCREMENT 4""");
        var unsupported = new Column("number", DbType.Int64, defaultExpression:
            ColumnDefaultExpression.NextSequenceValue(new("embedded\"quote", "main")));
        Assert.That(() => new SchemaScriptRenderer(Dialect()).Plan(new([new Table(identity, [unsupported], [])])),
            Throws.TypeOf<DubUrl.Schema.Renderers.ColumnDefaultPlanningException>());
        var column = new Column("number", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(sequence));
        var table = Create(connection, Dialect(), identity, column);
        var second = Create(connection, Dialect(), other, column);
        var discovered = await DiscoverAsync(connection, new DuckDbMetadataReader(), identity);
        Assert.That(discovered.Columns["number"].Generation, Is.Null);
        Assert.That(discovered.Columns["number"].DefaultExpression?.Sequence, Is.EqualTo(sequence));

        Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
        Execute(connection, $"INSERT INTO {second} DEFAULT VALUES");
        Execute(connection, $"INSERT INTO {table} VALUES (99)");
        Assert.That(Scalar(connection, $"SELECT MIN(number) FROM {table}"), Is.EqualTo(13));
        Assert.That(Scalar(connection, $"SELECT number FROM {second}"), Is.EqualTo(17));
        Assert.That(Scalar(connection, $"SELECT MAX(number) FROM {table}"), Is.EqualTo(99));

        var replacement = new SchemaScriptRenderer(Dialect(), SchemaCreationOptions.DropIfExists);
        foreach (var command in replacement.Plan(new([new Table(identity, [column], [])])))
            Execute(connection, command.CommandText);
        Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
        Assert.That(Scalar(connection, $"SELECT number FROM {table}"), Is.EqualTo(21));
    }

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        return builder.Build().Get<DuckDbDialect>();
    }
}
