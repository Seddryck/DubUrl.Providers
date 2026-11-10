using System.Data;
using DubUrl.Provider.Snowflake.Discovery;
using DubUrl.Provider.Snowflake.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Snowflake.Tests.Schema;

public class SnowflakeStructuredTypeTests
{
    private static readonly ScalarType Text = new(DbType.String);
    private static readonly ScalarType Integer = new(DbType.Int32);

    private static IEnumerable<TestCaseData> Representations()
    {
        yield return new TestCaseData(new StructuredColumnType(new JsonType(), StructuredStorage.Json), "VARIANT");
        yield return new TestCaseData(Native(new ArrayType(Integer, false)), "ARRAY(NUMBER(10, 0) NOT NULL)");
        yield return new TestCaseData(Native(new MapType(Text, new ArrayType(new ScalarType(DbType.Boolean), true), false)),
            "MAP(VARCHAR, ARRAY(BOOLEAN) NOT NULL)");
        yield return new TestCaseData(Native(new MapType(Integer, Text, true)), "MAP(NUMBER(10, 0), VARCHAR)");
        yield return new TestCaseData(Native(new StructType([
            new("item.id", Integer, false), new("item\"name", Text, true), new("payload", new JsonType(), true)
        ])), """OBJECT("item.id" NUMBER(10, 0) NOT NULL, "item""name" VARCHAR, "payload" VARIANT)""");
        yield return new TestCaseData(Native(new VectorType(DbType.Single, 4096)), "VECTOR(FLOAT, 4096)");
        yield return new TestCaseData(Native(new VectorType(DbType.Int32, 3)), "VECTOR(INT, 3)");
    }

    [TestCaseSource(nameof(Representations))]
    public void RendersAndDecodesTheQualifiedRepresentations(StructuredColumnType type, string sqlType)
    {
        Assert.That(Plan(type), Does.Contain(sqlType));
        Assert.That(SnowflakeTypeDeclaration.Parse(sqlType), Is.EqualTo(type.Shape));
    }

    [Test]
    public void QuotesAFieldWithSqlPunctuation()
    {
        var type = Native(new StructType([new("x\"); DROP TABLE y", Text, true)]));
        Assert.That(Plan(type), Does.Contain("""OBJECT("x""); DROP TABLE y" VARCHAR)"""));
    }

    [Test]
    public void JsonObjectShapeIsAdvisory()
    {
        var type = new StructuredColumnType(new JsonType(new StructType([new("id", Integer, false)])), StructuredStorage.Json);
        Assert.That(Plan(type), Does.Contain("VARIANT"));
        Assert.That(SnowflakeTypeDeclaration.Parse("VARIANT"), Is.EqualTo(new JsonType()));
    }

    private static IEnumerable<StructuredColumnType> UnsupportedTypes()
    {
        yield return new(new JsonType(), StructuredStorage.Binary);
        yield return new(new JsonType(), StructuredStorage.Native);
        yield return Native(new ArrayType(Integer, null));
        yield return Native(new ArrayType(new VectorType(DbType.Single, 3), true));
        yield return Native(new MapType(new ScalarType(DbType.Double), Text, true));
        yield return Native(new MapType(new ArrayType(Integer, true), Text, true));
        yield return Native(new StructType([new(new string('x', 256), Text, true)]));
        yield return Native(new ArrayType(new ScalarType(DbType.Decimal), true));
        yield return Native(new VectorType(DbType.Single, 4097));
        yield return Native(new VectorType(DbType.Double, 3));
    }

    [TestCaseSource(nameof(UnsupportedTypes))]
    public void RejectsUnsupportedDefinitionsBeforeDdl(StructuredColumnType type)
        => Assert.That(() => Plan(type), Throws.TypeOf<NotSupportedException>());

    [TestCase("ARRAY")]
    [TestCase("OBJECT")]
    [TestCase("OBJECT()")]
    [TestCase("OBJECT(a BOOLEAN, a BOOLEAN)")]
    [TestCase("OBJECT(\"unterminated VARCHAR)")]
    [TestCase("ARRAY(NUMBER(38,0))")]
    [TestCase("ARRAY(VARCHAR(12))")]
    [TestCase("MAP(FLOAT, VARCHAR)")]
    [TestCase("VECTOR(FLOAT, 0)")]
    [TestCase("VECTOR(INT, 9999999999999)")]
    [TestCase("ARRAY(BOOLEAN NOT FALSE)")]
    [TestCase("VECTOR(INT, 3); DROP TABLE x")]
    public void RejectsCatalogDeclarationsOutsideThePortableShape(string declaration)
        => Assert.That(SnowflakeTypeDeclaration.Parse(declaration), Is.Null);

    private static StructuredColumnType Native(StructuredType shape) => new(shape, StructuredStorage.Native);

    private static string Plan(StructuredColumnType type)
        => string.Join("\n", new SchemaScriptRenderer(Dialect()).Plan(new(
            [new Table("items", [new Column("payload", type)], [])])).Select(command => command.CommandText));

    internal static SnowflakeDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SnowflakeDialect>(["snowflake"]);
        return builder.Build().Get<SnowflakeDialect>();
    }
}
