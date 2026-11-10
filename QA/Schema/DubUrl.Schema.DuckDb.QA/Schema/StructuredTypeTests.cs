using System.Data;
using DuckDB.NET.Data;
using DubUrl.Discovery;
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
public sealed class StructuredTypeTests
{
    private static readonly ScalarType Integer = new(DbType.Int32);
    private static readonly ScalarType Text = new(DbType.String);

    [Test]
    public async Task JsonRoundTrips()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await StructuredTypeChecks.VerifyJsonAsync(connection, Dialect(), new DuckDbMetadataReader(), new("json_values", "main"));
    }

    [Test]
    public async Task ListsRoundTrip()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var type = new StructuredColumnType(new ArrayType(new ArrayType(Integer, true), true), StructuredStorage.Native);
        await StructuredTypeChecks.VerifyAsync(connection, Dialect(), new DuckDbMetadataReader(), new("list_values", "main"),
            type, "to_json(v)", "[[1, NULL], [], NULL]", "[[1,null],[],null]", "[]", "[]");
    }

    [Test]
    public async Task MapsRoundTrip()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var type = new StructuredColumnType(new MapType(Text, new ArrayType(Integer, true), true), StructuredStorage.Native);
        await StructuredTypeChecks.VerifyAsync(connection, Dialect(), new DuckDbMetadataReader(), new("map_values", "main"),
            type, "to_json(v)", "MAP(['a', 'b'], [[1, NULL], NULL])", "{\"a\":[1,null],\"b\":null}",
            "MAP([], [])", "{}", invalid: "MAP([NULL], [[1]])");
    }

    [Test]
    public async Task StructsRoundTripWithQuotedFields()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var shape = new StructType([new("select, \"value\"", new ArrayType(Integer, true), true), new("detail", Text, true)]);
        await StructuredTypeChecks.VerifyAsync(connection, Dialect(), new DuckDbMetadataReader(), new("struct_values", "main"),
            new(shape, StructuredStorage.Native), "to_json(v)",
            """{'select, "value"': [1, NULL], 'detail': NULL}""", """{"select, \"value\"":[1,null],"detail":null}""",
            """{'select, "value"': [], 'detail': NULL}""", """{"select, \"value\"":[],"detail":null}""");
    }

    [Test]
    public async Task VectorsRoundTripAndPreserveIndependentChecks()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var type = new StructuredColumnType(new VectorType(DbType.Single, 3), StructuredStorage.Native);
        const string quoted = "\"vector\"\"value\"";
        var schema = new DubUrl.Schema.Schema([new Table(new DatabaseObjectName("vector_values", "main"),
            [new Column("id", DbType.Int32), new Column("vector\"value", type, constraints: [new NullableConstraint()])],
            [new CheckConstraint(new ColumnIdentityExpression("id"), ">", new ValueExpression(0))])]);
        var renderer = new SchemaScriptRenderer(Dialect());
        Execute(connection, renderer.Plan(schema).Single().CommandText);
        Execute(connection, "INSERT INTO main.vector_values VALUES (1, [1.25,-2.5,3.5]), (2, NULL)");
        Assert.That(Scalar(connection, $"SELECT to_json({quoted}) FROM main.vector_values WHERE id=1"), Is.EqualTo("[1.25,-2.5,3.5]"));
        Assert.That(Scalar(connection, $"SELECT to_json({quoted}) FROM main.vector_values WHERE id=2"), Is.EqualTo(DBNull.Value));
        Assert.That(Scalar(connection, $"SELECT array_distance({quoted}, [1.25,-2.5,3.5]::FLOAT[3]) FROM main.vector_values WHERE id=1"),
            Is.EqualTo(0f));
        Assert.Catch<DuckDBException>(() => Execute(connection, "INSERT INTO main.vector_values VALUES (3, [1,2])"));
        Assert.Catch<DuckDBException>(() => Execute(connection, "INSERT INTO main.vector_values VALUES (3, [])"));
        Assert.Catch<DuckDBException>(() => Execute(connection, "INSERT INTO main.vector_values VALUES (3, [1,NULL,3])"));
        Assert.Catch<DuckDBException>(() => Execute(connection, $"UPDATE main.vector_values SET {quoted}=[1,2,NULL] WHERE id=1"));

        var result = await new SchemaInspector().InspectAsync(connection, new DuckDbMetadataReader(), new()
        {
            Tables = [new("vector_values", "main")],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Checks]
        });
        Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
        var discovered = result.Schema!.Tables.Values.Single();
        Assert.That(discovered.Columns["vector\"value"].StructuredType, Is.EqualTo(type));
        Assert.That(discovered.Constraints.OfType<CheckConstraint>().Single().Operator, Is.EqualTo(">"));
        var script = renderer.Plan(result.Schema).Single().CommandText;
        Assert.That(script.Split("list_count", StringSplitOptions.None), Has.Length.EqualTo(2));
        Execute(connection, "DROP TABLE main.vector_values");
        Execute(connection, script);
    }

    [TestCase(DbType.Int64, "[1,-2,3]", "[1,-2,3]")]
    [TestCase(DbType.Double, "[1.125,-2.25,3.5]", "[1.125,-2.25,3.5]")]
    public async Task OtherNumericVectorsRoundTrip(DbType component, string value, string expected)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var type = new StructuredColumnType(new VectorType(component, 3), StructuredStorage.Native);
        await StructuredTypeChecks.VerifyAsync(connection, Dialect(), new DuckDbMetadataReader(), new("vector_values", "main"),
            type, "to_json(v)", value, expected, invalid: "[1,NULL,3]");
    }

    [Test]
    public async Task DiscoveryDiagnosesFixedArraysAndLeavesJsonTextAsText()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var table = Create(connection, Dialect(), new("json_text_values", "main"),
            new Column("payload", new StructuredColumnType(new JsonType(), StructuredStorage.JsonText)));
        Execute(connection, $"INSERT INTO {table} VALUES ('not json')");
        Assert.That(Scalar(connection, $"SELECT payload FROM {table}"), Is.EqualTo("not json"));
        Execute(connection, "CREATE TABLE fixed_values (payload FLOAT[3])");

        var reader = new DuckDbMetadataReader();
        var text = await DiscoverAsync(connection, reader, new("json_text_values", "main"));
        Assert.That(text.Columns["payload"].StructuredType, Is.Null);
        var fixedArray = await new SchemaInspector().InspectAsync(connection, reader, new()
        {
            Tables = [new("fixed_values", "main")],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns],
            AllowPartial = true
        });
        Assert.That(fixedArray.Diagnostics.Any(item => item.Code == "SDI012"
            && item.Coverage == MetadataCoverage.Unrepresentable), Is.True);
    }

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        return builder.Build().Get<DuckDbDialect>();
    }
}
