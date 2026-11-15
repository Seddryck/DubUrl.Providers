using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.Snowflake.Discovery;
using DubUrl.Provider.Snowflake.Mapping;
using DubUrl.Provider.Snowflake.Tests.Schema;
using DubUrl.Schema;
using DubUrl.Schema.Documents;
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
    public async Task RejectedScalarUsesTheCompleteDescribeDeclaration()
    {
        var commands = new List<CommandLog>();
        var connection = Catalog(commands, () => Tables(Identity),
            () => Columns(Column(Identity, "stamp", 1, "TIMESTAMP_TZ", "YES"), Column(Identity, "id", 2, "NUMBER", "NO", 10, 0)),
            () => Describe(["stamp", "TIMESTAMP_TZ(3)"], ["id", "NUMBER(10,0)"]));
        var result = await new SnowflakeMetadataReader().ReadAsync(connection.Object, Options);
        var rejection = result.Diagnostics.Single(diagnostic => diagnostic.Code == "SDI009");
        Assert.That(rejection.Object, Is.EqualTo(Identity));
        Assert.That(rejection.Member, Is.EqualTo("stamp"));
        Assert.That(rejection.NativeDefinition, Is.EqualTo("TIMESTAMP_TZ(3)"));
        Assert.That(result.Tables.Single().Columns[1].Type, Is.EqualTo(DbType.Decimal));
        Assert.That(commands, Has.Count.EqualTo(3));
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

    [Test]
    public async Task DirectTableTagsSurviveSchemaDocumentAndGenerateQualifiedAssignments()
    {
        var commands = new List<CommandLog>();
        var connection = Catalog(commands, () => Tables(Identity, Identity with { Name = "unselected" }),
            () => Columns(Column(Identity, "id", 1, "NUMBER", "NO", 10, 0)),
            () => TagRows(["GOV", "TAGS", "OWNER", "L'équipe\\data"],
                ["GOV", "Other.Schema", "OWNER", "finance"], ["GOV", "TAGS", "Quoted\"Tag", ""]));
        var options = Options with { Features = [.. Options.Features, SchemaFeature.Annotations] };
        var result = await new SchemaInspector().InspectAsync(connection.Object, new SnowflakeMetadataReader(), options);

        Assert.That(result.IsComplete, Is.True);
        Assert.That(commands, Has.Count.EqualTo(3));
        Assert.That(commands[2].Sql, Does.Contain("FROM TABLE(\"Test\"\"Db\".INFORMATION_SCHEMA.TAG_REFERENCES(?, 'TABLE'))")
            .And.Contain("DOMAIN = 'TABLE' AND LEVEL = 'TABLE' AND COLUMN_NAME IS NULL")
            .And.Contain("APPLY_METHOD = 'MANUAL' AND TAG_DATABASE <> 'SNOWFLAKE'"));
        Assert.That(commands[2].Bindings, Is.EqualTo(new[] { "\"Test\"\"Db\".\"Mixed.Schema\".\"a\"\"b\"" }));
        var annotations = new Dictionary<string, string>
        {
            ["GOV.TAGS.OWNER"] = "L'équipe\\data",
            ["GOV.\"Other.Schema\".OWNER"] = "finance",
            ["GOV.TAGS.\"Quoted\"\"Tag\""] = "",
        };
        Assert.That(result.Schema!.Tables.Values.Single().Annotations, Is.EquivalentTo(annotations));

        using var stream = new MemoryStream();
        new SchemaDocumentWriter().Write(stream, new SchemaDocumentExporter().Map(result.Schema), SchemaDocumentFormat.Json);
        stream.Position = 0;
        var schema = new SchemaDocumentMapper().Map(new SchemaDocumentReader().Read(stream, SchemaDocumentFormat.Json));
        var plan = new SchemaScriptRenderer(SnowflakeStructuredTypeTests.Dialect()).Plan(schema);
        Assert.That(plan[0].CommandText, Does.StartWith("CREATE TABLE \"Test\"\"Db\".\"Mixed.Schema\".\"a\"\"b\""));
        Assert.That(plan[1].CommandText, Is.EqualTo(
            """ALTER TABLE "Test""Db"."Mixed.Schema"."a""b" SET TAG "GOV"."Other.Schema"."OWNER" = 'finance', "GOV"."TAGS"."Quoted""Tag" = '', "GOV"."TAGS"."OWNER" = 'L''équipe\\data';"""));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReportsTagReadFailuresAndMultiValueAssignments(bool inaccessible)
    {
        var options = Options with { Features = [.. Options.Features, SchemaFeature.Annotations], AllowPartial = true };
        var connection = Catalog([], () => Tables(Identity),
            () => Columns(Column(Identity, "id", 1, "NUMBER", "NO", 10, 0)),
            () => inaccessible ? throw new CatalogException() : TagRows(
                ["GOV", "TAGS", "REGION", "eu"], ["GOV", "TAGS", "REGION", "na"],
                ["GOV", "TAGS", "OWNER", "finance"]));
        ISchemaMetadataReader reader = new SnowflakeMetadataReader();
        var metadata = await reader.ReadAsync(connection.Object, options);
        var result = new SchemaInspector().Map(metadata, options);
        var diagnostic = result.Diagnostics.Single(item => item.Feature == SchemaFeature.Annotations && item.Object == Identity);
        Assert.That(diagnostic.Coverage, Is.EqualTo(inaccessible ? MetadataCoverage.Inaccessible : MetadataCoverage.Unrepresentable));
        Assert.That(diagnostic.Message, Does.Not.Contain("private driver detail"));
        Assert.That(result.Schema!.Tables.Values.Single().Annotations.Keys,
            Is.EqualTo(inaccessible ? Array.Empty<string>() : new[] { "GOV.TAGS.OWNER" }));
    }

    private static DataTable TagRows(params object?[][] rows)
        => Result(["TAG_DATABASE", "TAG_SCHEMA", "TAG_NAME", "TAG_VALUE"], rows);

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
