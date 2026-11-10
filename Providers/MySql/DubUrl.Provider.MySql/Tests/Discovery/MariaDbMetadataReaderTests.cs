using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Schema;
using NUnit.Framework;
using static DubUrl.Provider.MySql.Tests.Discovery.MySqlMetadataReaderTests;

namespace DubUrl.Provider.MySql.Tests.Discovery;

public class MariaDbMetadataReaderTests
{
    [Test]
    public async Task ColumnChecksIdentifyJsonAndVectorDeclarationsIdentifyDimensions()
    {
        var tables = Table(["database_name", "table_name"], ["db", "t"]);
        var columns = Table(["database_name", "table_name", "column_name", "ordinal", "type_name", "native_type_name", "nullable", "collation_name"],
            ["db", "t", "body`doc", 1, "longtext", "longtext", "YES", "utf8mb4_bin"],
            ["db", "t", "plain", 2, "longtext", "longtext", "YES", "utf8mb4_bin"],
            ["db", "t", "embedding", 3, "vector", "vector(1536)", "NO", null],
            ["db", "t", "other", 4, "longtext", "longtext", "YES", "utf8mb4_bin"],
            ["db", "t", "id", 5, "int", "int(11)", "NO", null]);
        var checks = Table(["database_name", "table_name", "check_name", "expression", "json_candidate"],
            ["db", "t", "body`doc", "json_valid(`body``doc`)", "body`doc"],
            ["db", "t", "plain", "json_valid(`plain`)", null],
            ["db", "t", "other", "json_valid(`other`) = 0", "other"],
            ["db", "t", "positive", "`id` > 0", null],
            ["db", "different_table", "other", "json_valid(`other`)", "other"]);
        var connection = Catalog(tables, columns, checks, checks);
        connection.SetupGet(c => c.ServerVersion).Returns("5.5.5-11.8.9-MariaDB");

        var result = await new MariaDbMetadataReader().ReadWithStructuredTypesAsync(connection.Object,
            new() { Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Checks] }, default);

        var table = result.Metadata.Tables.Single();
        Assert.That(result.StructuredColumns.Count, Is.EqualTo(2));
        Assert.That(result.StructuredColumns[new(table.Identity, "body`doc")].Type,
            Is.EqualTo(new StructuredColumnType(new JsonType(), StructuredStorage.JsonText)));
        Assert.That(result.StructuredColumns[new(table.Identity, "embedding")].Type,
            Is.EqualTo(new StructuredColumnType(new VectorType(DbType.Single, 1536), StructuredStorage.Native)));
        Assert.That(table.Columns.Single(column => column.Name == "plain").NativeType, Is.EqualTo("LONGTEXT"));
        Assert.That(table.Checks.Single().Name, Is.EqualTo("positive"));
        Assert.That(result.Metadata.Diagnostics.Where(item => item.Feature == SchemaFeature.Checks), Has.Exactly(2).Items);
    }

    [TestCase("vector")]
    [TestCase("vector(0)")]
    [TestCase("vector(16384)")]
    [TestCase("vector(3, FLOAT64)")]
    public async Task InvalidVectorDeclarationReportsIncompleteDiscovery(string declaration)
    {
        var connection = Catalog(Table(["database_name", "table_name"], ["db", "t"]),
            Table(["database_name", "table_name", "column_name", "ordinal", "type_name", "native_type_name", "nullable"],
                ["db", "t", "embedding", 1, "vector", declaration, "YES"]));
        var result = await new SchemaInspector().InspectAsync(connection.Object, new MariaDbMetadataReader(),
            new() { Features = [SchemaFeature.Tables, SchemaFeature.Columns] });

        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Diagnostics.Any(item => item.Code == "SDI012" && item.Coverage == MetadataCoverage.Unrepresentable), Is.True);
    }
}
