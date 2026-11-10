using System.Data;
using DuckDB.NET.Data;
using DubUrl.Discovery;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
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
