using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.Oracle.Discovery;
using DubUrl.Provider.Oracle.QA;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using NUnit.Framework;
using Oracle.ManagedDataAccess.Client;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.Oracle.QA;

[TestFixture]
[Category("Oracle")]
[Category("Schema")]
public sealed class StructuredTypeTests
{
    [Test]
    public async Task NativeJsonRoundTrips()
    {
        using var schema = OracleTestDatabase.CreateSchema();
        using var connection = schema.Connect();
        await connection.OpenAsync();
        await StructuredTypeChecks.VerifyAsync(connection, Dialect(connection), new OracleMetadataReader(),
            new("NATIVE_JSON_VALUES", schema.Name), new(new JsonType(), StructuredStorage.Json),
            """JSON_SERIALIZE("v" RETURNING VARCHAR2(4000))""",
            """JSON('{"items":[1,null,{"ok":true}]}')""", """{"items":[1,null,{"ok":true}]}""",
            "JSON('{}')", "{}", "JSON('null')", "null", invalid: "JSON('[')");
    }

    [TestCase(DbType.SByte)]
    [TestCase(DbType.Single)]
    [TestCase(DbType.Double)]
    public async Task FixedDenseVectorsRoundTrip(DbType elementType)
    {
        using var schema = OracleTestDatabase.CreateSchema();
        using var connection = schema.Connect();
        await connection.OpenAsync();
        await StructuredTypeChecks.VerifyAsync(connection, Dialect(connection), new OracleMetadataReader(),
            new("NATIVE_VECTOR_VALUES", schema.Name), new(new VectorType(elementType, 3), StructuredStorage.Native),
            """VECTOR_SERIALIZE("v" RETURNING VARCHAR2(4000))""",
            "TO_VECTOR('[1,-2,3]')", "[1,-2,3]", invalid: "TO_VECTOR('[1,2]')");
    }

    [TestCase("VECTOR")]
    [TestCase("VECTOR(8, BINARY)")]
    [TestCase("VECTOR(3, FLOAT32, SPARSE)")]
    [TestCase("JSON(OBJECT)")]
    public async Task DiscoveryDiagnosesUnsupportedCatalogShapes(string typeSql)
    {
        using var schema = OracleTestDatabase.CreateSchema();
        using var connection = schema.Connect();
        await connection.OpenAsync();
        Execute(connection, $"CREATE TABLE UNSUPPORTED_SHAPE (payload {typeSql})");
        var result = await new SchemaInspector().InspectAsync(connection, new OracleMetadataReader(), new()
        {
            Tables = [new("UNSUPPORTED_SHAPE", schema.Name)],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns],
            AllowPartial = true
        });
        Assert.That(result.Diagnostics.Any(item => item.Code == "SDI012"
            && item.Coverage == MetadataCoverage.Unrepresentable), Is.True);
    }

    private static OracleDialect Dialect(OracleConnection connection)
    {
        var version = Version.Parse(string.Join('.', connection.ServerVersion.Split('.').Take(2)));
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<OracleDialect>(["oracle"]);
        return builder.Build().Get<OracleDialect>().WithServerVersion(version);
    }
}
