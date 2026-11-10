using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.Snowflake.Discovery;
using DubUrl.Provider.Snowflake.Mapping;
using DubUrl.Schema;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Snowflake.Data.Client;

namespace DubUrl.Provider.Snowflake.Tests.Discovery;

public class SnowflakeMetadataReaderTests
{
    private static readonly MetadataName Identity = new("a\"b", "Mixed.Schema", "Test\"Db");
    private static readonly ScalarType Integer = new(DbType.Int32);
    private static readonly ScalarType Text = new(DbType.String);
    private static DiscoveryOptions Options => new()
    {
        Tables = [Identity],
        Features = [SchemaFeature.Tables, SchemaFeature.Columns]
    };

    [Test]
    public void MapperRegistersSnowflakeDiscovery()
        => Assert.That(((SchemaDiscoveryAttribute)Attribute.GetCustomAttribute(typeof(SnowflakeMapper),
            typeof(SchemaDiscoveryAttribute))!).Create().Reader, Is.TypeOf<SnowflakeMetadataReader>());

    [Test]
    public async Task InspectionPreservesTypesNullabilityAndQualifiedNames()
    {
        var other = Identity with { Name = "unselected" };
        var commands = new List<CommandLog>();
        var connection = Catalog(commands,
            () => Tables(Identity, other),
            () => Columns(
                Column(Identity, "id", 1, "NUMBER", "NO", 10, 0),
                Column(Identity, "json", 2, "VARIANT", "YES"),
                Column(Identity, "list", 3, "ARRAY", "NO"),
                Column(Identity, "map", 4, "MAP", "YES"),
                Column(Identity, "struct", 5, "OBJECT", "YES"),
                Column(Identity, "vector", 6, "VECTOR", "YES"),
                Column(other, "skipped", 1, "ARRAY", "YES")),
            () => Describe(
                ["list", "ARRAY(NUMBER(10,0) NOT NULL)"],
                ["map", "map(VARCHAR(16777216), ARRAY(BOOLEAN) NOT NULL)"],
                ["struct", """OBJECT("item.id" NUMBER(10,0) NOT NULL, "MixedCase" VARIANT)"""],
                ["vector", "VECTOR(FLOAT, 12)"]));

        var result = await new SchemaInspector().InspectAsync(connection.Object, new SnowflakeMetadataReader(), Options);
        Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
        var table = result.Schema!.Tables.Values.Single();
        Assert.That(table.Identity, Is.EqualTo(new DatabaseObjectName(Identity.Name, Identity.Schema, Identity.Database)));
        Assert.That(table.Columns["id"].Type, Is.EqualTo(DbType.Decimal));
        Assert.That(table.Columns["json"].StructuredType, Is.EqualTo(new StructuredColumnType(new JsonType(), StructuredStorage.Json)));
        Assert.That(table.Columns["list"].StructuredType, Is.EqualTo(Native(new ArrayType(Integer, false))));
        Assert.That(table.Columns["list"].Constraints.Get<DubUrl.Schema.Constraints.NotNullableConstraint>(), Is.Not.Null);
        Assert.That(table.Columns["map"].StructuredType,
            Is.EqualTo(Native(new MapType(Text, new ArrayType(new ScalarType(DbType.Boolean), true), false))));
        Assert.That(table.Columns["struct"].StructuredType,
            Is.EqualTo(Native(new StructType([new("item.id", Integer, false), new("MixedCase", new JsonType(), true)]))));
        Assert.That(table.Columns["vector"].StructuredType, Is.EqualTo(Native(new VectorType(DbType.Single, 12))));

        Assert.That(commands, Has.Count.EqualTo(3));
        Assert.That(commands[0].Sql, Does.Contain("FROM \"Test\"\"Db\".INFORMATION_SCHEMA.TABLES"));
        Assert.That(commands[1].Sql, Does.Contain("FROM \"Test\"\"Db\".INFORMATION_SCHEMA.COLUMNS"));
        foreach (var command in commands.Take(2))
        {
            Assert.That(command.Sql, Does.EndWith("AND t.table_schema IN (?) AND t.table_name IN (?)"));
            Assert.That(command.Bindings, Is.EqualTo(new[] { Identity.Schema, Identity.Name }));
        }
        Assert.That(commands[2].Sql, Is.EqualTo("DESCRIBE TABLE \"Test\"\"Db\".\"Mixed.Schema\".\"a\"\"b\""));
        connection.Verify(item => item.Close(), Times.Never);
    }

    [Test]
    public async Task UsesCompleteCatalogDeclarationsWithoutAnExtraQuery()
    {
        var commands = new List<CommandLog>();
        var connection = Catalog(commands, () => Tables(Identity),
            () => Columns(Column(Identity, "vector", 1, "VECTOR(INT, 4)", "YES")));
        var result = await new SnowflakeMetadataReader().ReadWithStructuredTypesAsync(connection.Object, Options, CancellationToken.None);
        Assert.That(result.StructuredColumns[new(Identity, "vector")].Type, Is.EqualTo(Native(new VectorType(DbType.Int32, 4))));
        Assert.That(commands, Has.Count.EqualTo(2));
    }

    [TestCase(null)]
    [TestCase("ARRAY")]
    [TestCase("ARRAY(NUMBER(38,0))")]
    [TestCase("OBJECT(field VARCHAR)")]
    public async Task ReportsIncompleteDeclarationsWithoutGuessing(string? declaration)
    {
        var connection = Catalog([], () => Tables(Identity), () => Columns(Column(Identity, "data", 1, "ARRAY", "YES")),
            () => declaration is null ? Describe() : Describe(["data", declaration]));
        var result = await new SchemaInspector().InspectAsync(connection.Object, new SnowflakeMetadataReader(), Options);
        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Diagnostics.Single(item => item.Code == "SDI012").Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
    }

    [Test]
    public async Task DescribeFailureKeepsColumnInventoryAndSanitizesDiagnostics()
    {
        var connection = Catalog([], () => Tables(Identity),
            () => Columns(Column(Identity, "data", 1, "OBJECT", "YES")), () => throw new CatalogException());
        var result = await new SchemaInspector().InspectAsync(connection.Object, new SnowflakeMetadataReader(), Options);
        Assert.That(result.Diagnostics.Single(item => item.Code == "SDI012").Coverage, Is.EqualTo(MetadataCoverage.Inaccessible));
        Assert.That(string.Join(", ", result.Diagnostics.Select(item => item.Message)), Does.Not.Contain("private driver detail"));
    }

    private static StructuredColumnType Native(StructuredType shape) => new(shape, StructuredStorage.Native);

    private static DataTable Tables(params MetadataName[] tables)
        => Result(["database_name", "schema_name", "table_name"], tables.Select(table =>
            new object?[] { table.Database, table.Schema, table.Name }).ToArray());

    private static object?[] Column(MetadataName table, string name, int ordinal, string type, string nullable,
        int? precision = null, int? scale = null)
        => [table.Database, table.Schema, table.Name, name, ordinal, type, nullable, precision, scale];

    private static DataTable Columns(params object?[][] columns)
        => Result(["database_name", "schema_name", "table_name", "column_name", "ordinal", "type_name", "nullable", "precision", "scale"], columns);

    private static DataTable Describe(params object?[][] columns) => Result(["name", "type"], columns);

    private static DataTable Result(string[] fields, object?[][] rows)
    {
        var table = new DataTable();
        foreach (var field in fields)
            table.Columns.Add(field, typeof(object));
        foreach (var row in rows)
            table.Rows.Add(row.Select(item => item ?? DBNull.Value).ToArray());
        return table;
    }

    private static Mock<DbConnection> Catalog(List<CommandLog> commands, params Func<DataTable>[] results)
    {
        var remaining = new Queue<Func<DataTable>>(results);
        var connection = new Mock<DbConnection>();
        connection.SetupGet(item => item.State).Returns(ConnectionState.Open);
        connection.Protected().Setup<DbCommand>("CreateDbCommand").Returns(() =>
        {
            var command = new Mock<DbCommand>();
            var parameters = new SnowflakeDbCommand().Parameters;
            command.SetupProperty(item => item.CommandText);
            command.Protected().SetupGet<DbParameterCollection>("DbParameterCollection").Returns(parameters);
            command.Protected().Setup<DbParameter>("CreateDbParameter").Returns(() => new SnowflakeDbParameter());
            command.Protected().Setup<Task<DbDataReader>>("ExecuteDbDataReaderAsync",
                ItExpr.IsAny<CommandBehavior>(), ItExpr.IsAny<CancellationToken>()).Returns(() =>
            {
                commands.Add(new(command.Object.CommandText, parameters.Cast<DbParameter>().Select(item => item.Value).ToArray()));
                return Task.FromResult<DbDataReader>(remaining.Dequeue()().CreateDataReader());
            });
            return command.Object;
        });
        return connection;
    }

    private sealed record CommandLog(string Sql, object?[] Bindings);
    private sealed class CatalogException() : DbException("private driver detail");
}
