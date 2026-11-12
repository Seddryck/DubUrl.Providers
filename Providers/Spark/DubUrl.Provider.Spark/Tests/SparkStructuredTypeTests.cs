using System.Data;
using DubUrl.Mapping;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.Tests;

public sealed class SparkStructuredTypeTests
{
    private static readonly ScalarType Integer = new(DbType.Int32);
    private static readonly ScalarType Text = new(DbType.String);
    private static readonly StructType RequiredField = new([new("n", Integer, false)]);

    [TestCase(false)]
    [TestCase(true)]
    public void AllReversibleLeavesRenderAndReadInFieldOrder(bool delta)
    {
        var leaves = new (DbType Type, string Sql)[]
        {
            (DbType.Boolean, "BOOLEAN"), (DbType.SByte, "TINYINT"), (DbType.Int16, "SMALLINT"),
            (DbType.Int32, "INT"), (DbType.Int64, "BIGINT"), (DbType.Single, "FLOAT"), (DbType.Double, "DOUBLE"),
            (DbType.String, "STRING"), (DbType.Binary, "BINARY"), (DbType.Date, "DATE"),
        };
        var type = new StructType(leaves.Select((leaf, i) => new StructField($"f{i}", new ScalarType(leaf.Type), true)));
        var expected = "STRUCT<" + string.Join(", ", leaves.Select((leaf, i) => $"`f{i}`: {leaf.Sql}")) + ">";
        var plan = Plan(Dialect(delta), type);
        Assert.That(plan.Single().CommandText, Does.Contain(expected));
        Assert.That(SparkCatalogDefinition.StructuredSql(SparkCatalogDefinition.Columns(plan.Single().CommandText)["v"]), Is.EqualTo(expected));
        Assert.That(leaves.Select(leaf => SparkStructuredTypes.ReadScalar(leaf.Sql.ToLowerInvariant())),
            Is.EqualTo(leaves.Select(leaf => new ScalarType(leaf.Type))));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectsUnsupportedStorageAndShapeBeforeDdl(bool delta)
    {
        var dialect = Dialect(delta);
        var nullable = new StructType([new("n", Integer, true)]);
        Reject(dialect, nullable, "Binary storage", StructuredStorage.Binary);
        Reject(dialect, new JsonType(nullable), "Shaped JSON", StructuredStorage.Json);
        Reject(dialect, new JsonType(nullable), "Shaped JSON", StructuredStorage.JsonText);
        Reject(dialect, new ArrayType(Integer, false), "nullable elements");
        Reject(dialect, new MapType(Text, Integer, false), "nullable values");
        Reject(dialect, new MapType(nullable, Integer, true), "scalar key");
        Reject(dialect, new ArrayType(Integer, null), "incomplete nested nullability");
        Reject(dialect, new MapType(Text, Integer, null), "incomplete nested nullability");
        Reject(dialect, new StructType([new("n", Integer, null)]), "incomplete nested nullability");
        Reject(dialect, new ArrayType(new ScalarType(DbType.Decimal), true), "reversible");
        Reject(dialect, new ArrayType(new ScalarType(DbType.DateTime2), true), "reversible");
        Reject(dialect, new ArrayType(new JsonType(), true), "reversible");
        Reject(dialect, new StructType([new("Name", Text, true), new("name", Text, true)]), "collides");
        Reject(dialect, new VectorType(DbType.Double, 2), "Fixed-length vectors");
        Reject(dialect, new JsonType(), delta ? "WithVariantType()" : "no VARIANT", StructuredStorage.Json);
    }

    [Test]
    public void SparkCannotPlanRequiredFieldsOrVectors()
    {
        var dialect = Dialect(false);
        Reject(dialect, RequiredField, "cannot enforce NOT NULL");
        Reject(dialect, new ArrayType(Integer, true), "cannot enforce NOT NULL", required: true);
        Reject(dialect, new VectorType(DbType.Single, 3), "Fixed-length vectors");
    }

    [Test]
    public void DeltaRequiresEntireStructPathAndSupportsOptIns()
    {
        var delta = (DatabricksSqlDialect)Dialect(true);
        Reject(delta, RequiredField, "enclosing structs");
        Reject(delta, new StructType([new("nested", RequiredField, true)]), "enclosing structs", required: true);
        Reject(delta, new ArrayType(RequiredField, true), "no ARRAY or MAP ancestor", required: true);
        Reject(delta, new MapType(Text, RequiredField, true), "no ARRAY or MAP ancestor", required: true);
        Assert.That(Plan(delta, new StructType([new("nested", RequiredField, false)]), required: true)
            .Single().CommandText, Does.Contain("STRUCT<`nested`: STRUCT<`n`: INT NOT NULL> NOT NULL> NOT NULL"));

        var quoted = new StructType([new("a b`", Text, true)]);
        Reject(delta, quoted, "WithNameBasedColumnMapping");
        Assert.That(Plan(delta.WithNameBasedColumnMapping(), quoted).Single().CommandText,
            Does.Contain("STRUCT<`a b```: STRING>").And.Contain("'delta.columnMapping.mode' = 'name'"));
        Assert.That(Plan(delta.WithVariantType(), new JsonType(), StructuredStorage.Json).Single().CommandText, Does.Contain("VARIANT"));
    }

    [Test]
    public void VectorPlanInstallsAnIndependentNullSafeCheckPerColumn()
    {
        var dialect = Dialect(true);
        var vector = new StructuredColumnType(new VectorType(DbType.Single, 3), StructuredStorage.Native);
        var model = new DubUrl.Schema.Schema([new Table(new DatabaseObjectName("v-table", "default", catalog: "catalog"), [
            new Column("first`", vector), new Column("second", vector, constraints: [new NotNullableConstraint()]),
        ])]);
        var renderer = new SchemaScriptRenderer(dialect, SchemaCreationOptions.DropIfExists);
        var commands = renderer.Plan(model);
        Assert.That(commands, Has.Count.EqualTo(4));
        Assert.That(commands[0].CommandText, Does.StartWith("DROP TABLE"));
        Assert.That(commands[1].CommandText, Does.Contain("`first``` ARRAY<FLOAT>").And.Contain("`second` ARRAY<FLOAT> NOT NULL")
            .And.Not.Contain("/*"));
        Assert.That(commands[2].CommandText, Does.Contain("ALTER TABLE `catalog`.`default`.`v-table` ADD CONSTRAINT")
            .And.Contain("array_compact(`first```)").And.Contain("= 3"));
        Assert.That(commands[3].CommandText, Does.Contain("array_compact(`second`)"));
        Assert.That(renderer.Render(model), Does.Contain(commands[3].CommandText));
        Assert.That(DeltaVectorSql.ReadDimensions("first`", "`first``` IS NULL OR (size(array_compact(`first```)) = size(`first```) AND size(`first```) = 3)"), Is.EqualTo(3));
        Assert.That(DeltaVectorSql.ReadDimensions("first`", "size(`first```) = 3"), Is.Null);
    }

    private static SparkSqlDialect Dialect(bool delta) => delta
        ? DefaultRegistry.GetDialects("odbc+databricks").Get<DatabricksSqlDialect>()
        : DefaultRegistry.GetDialects("odbc+spark").Get<SparkSqlDialect>();

    private static IReadOnlyList<SchemaSqlCommand> Plan(IDialect dialect, StructuredType type,
        StructuredStorage storage = StructuredStorage.Native, bool required = false)
        => new SchemaScriptRenderer(dialect).Plan(new DubUrl.Schema.Schema([new Table("t", [
            new Column("v", new StructuredColumnType(type, storage), constraints: required ? [new NotNullableConstraint()] : [new NullableConstraint()]),
        ])]));

    private static void Reject(IDialect dialect, StructuredType type, string message,
        StructuredStorage storage = StructuredStorage.Native, bool required = false)
        => Assert.That(() => Plan(dialect, type, storage, required), Throws.TypeOf<NotSupportedException>().With.Message.Contains(message));
}
