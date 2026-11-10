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

    [Test]
    public async Task VarraysRoundTripWithDeclaredCapacityAndNullableElements()
    {
        using var schema = OracleTestDatabase.CreateSchema(createTypes: true);
        using var connection = schema.Connect();
        await connection.OpenAsync();
        var shape = new ArrayType(new ScalarType(DbType.Int32), true);
        var name = new DatabaseObjectName("ITEMS_T", schema.Name);
        var dialect = Dialect(connection).WithVarrayType(name, shape, 3);
        foreach (var command in dialect.RenderNamedTypeDefinitions())
            Execute(connection, command);

        await StructuredTypeChecks.VerifyAsync(connection, dialect, new OracleMetadataReader(), new("ARRAY_VALUES", schema.Name),
            new(shape, StructuredStorage.Native), """CASE WHEN "v" IS NULL THEN NULL ELSE JSON_ARRAY("v" NULL ON NULL RETURNING VARCHAR2(4000)) END""",
            "ITEMS_T(3,NULL,1)", "[3,null,1]", "ITEMS_T()", "[]", invalid: "ITEMS_T(1,2,3,4)");

        Execute(connection, "CREATE TABLE TYPE_METADATA (V ITEMS_T)");
        var metadata = await new OracleMetadataReader().ReadWithStructuredTypesAsync(connection, new()
        {
            Schemas = [schema.Name],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns]
        }, default);
        var raw = metadata.Metadata.Tables.Single(table => table.Identity.Name == "TYPE_METADATA").Columns.Single();
        Assert.That(raw.NativeType, Is.EqualTo($"\"{schema.Name}\".\"ITEMS_T\""));
        Assert.That(raw.Length, Is.EqualTo(3));
    }

    [Test]
    public async Task ObjectColumnsRoundTripWithQuotedAttributesAndNestedVarrays()
    {
        using var schema = OracleTestDatabase.CreateSchema(createTypes: true);
        using var connection = schema.Connect();
        await connection.OpenAsync();
        var items = new ArrayType(new ScalarType(DbType.Int32), true);
        var shape = new StructType([new("item list", items, true), new("Label", new ScalarType(DbType.String), true)]);
        var dialect = Dialect(connection).WithVarrayType(new("ITEMS_T", schema.Name), items, 3)
            .WithObjectType(new("PAYLOAD_T", schema.Name), shape);
        foreach (var command in dialect.RenderNamedTypeDefinitions())
            Execute(connection, command);

        await StructuredTypeChecks.VerifyAsync(connection, dialect, new OracleMetadataReader(), new("OBJECT_VALUES", schema.Name),
            new(shape, StructuredStorage.Native), """CASE WHEN "v" IS NULL THEN NULL ELSE JSON_OBJECT("v" NULL ON NULL RETURNING VARCHAR2(4000)) END""",
            "PAYLOAD_T(ITEMS_T(2,NULL,1), 'ready')", """{"item list":[2,null,1],"Label":"ready"}""",
            "PAYLOAD_T(ITEMS_T(), 'empty')", """{"item list":[],"Label":"empty"}""");
    }

    [Test]
    public async Task DiscoveryReportsUnorderedCollectionsAndNonPortableObjectAttributes()
    {
        using var schema = OracleTestDatabase.CreateSchema(createTypes: true);
        using var connection = schema.Connect();
        await connection.OpenAsync();
        Execute(connection, "CREATE TYPE UNORDERED_T AS TABLE OF NUMBER(10,0)");
        Execute(connection, "CREATE TYPE BOUNDED_T AS OBJECT (V VARCHAR2(8 BYTE))");
        Execute(connection, "CREATE TYPE REQUIRED_T AS VARRAY(3) OF NUMBER(10,0) NOT NULL");
        Execute(connection, """
            CREATE TABLE OUTSIDE_MODEL (ITEMS UNORDERED_T, OBJ BOUNDED_T, REQUIRED_ITEMS REQUIRED_T)
            NESTED TABLE ITEMS STORE AS UNORDERED_STORAGE
            """);

        var result = await new OracleMetadataReader().ReadWithStructuredTypesAsync(connection, new()
        {
            Schemas = [schema.Name],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns]
        }, default);
        var identity = new MetadataName("OUTSIDE_MODEL", Schema: schema.Name);
        foreach (var column in new[] { "ITEMS", "OBJ", "REQUIRED_ITEMS" })
            Assert.That(result.StructuredColumns[new(identity, column)], Is.EqualTo(new StructuredColumnMetadata(null, MetadataCoverage.Unrepresentable)));
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
