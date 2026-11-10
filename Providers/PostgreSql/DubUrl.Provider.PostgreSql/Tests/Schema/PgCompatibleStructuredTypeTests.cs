using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.Mapping;
using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public class PgCompatibleStructuredTypeTests
{
    internal static readonly ScalarType Integer = new(DbType.Int32);
    internal static readonly ScalarType DoubleLeaf = new(DbType.Double);
    internal static StructuredColumnType Native(StructuredType type) => new(type, StructuredStorage.Native);

    [TestCase(typeof(CockRoachMapper), typeof(CockroachMetadataReader))]
    [TestCase(typeof(CrateDbMapper), typeof(CrateDbMetadataReader))]
    [TestCase(typeof(QuestDbMapper), typeof(QuestDbMetadataReader))]
    public void MappersRegisterEngineSpecificDiscovery(Type mapper, Type reader)
        => Assert.That(((SchemaDiscoveryAttribute)Attribute.GetCustomAttribute(mapper, typeof(SchemaDiscoveryAttribute))!).Create().Reader,
            Is.TypeOf(reader));

    [Test]
    public void CockroachPlansJsonArraysAndQualifiedVectors()
    {
        var dialect = Dialect<CockRoachDialect>();
        Assert.That(Plan(dialect, new(new JsonType(), StructuredStorage.Json)), Does.Contain(" JSONB"));
        Assert.That(Plan(dialect, Native(new ArrayType(Integer, true))), Does.Contain(" INT4[]"));
        var vector = Native(new VectorType(DbType.Single, 3));
        Assert.That(Plan(dialect.WithServerVersion(new(24, 2)), vector), Does.Contain(" VECTOR(3)"));
        Reject(dialect.WithServerVersion(new(24, 1)), vector);
        Reject(dialect.WithServerVersion(new(24, 2)), Native(new VectorType(DbType.Single, 16001)));
        Reject(dialect.WithServerVersion(new(24, 2)), Native(new VectorType(DbType.Double, 3)));
        Reject(dialect, Native(new ArrayType(Integer, false)));
        Reject(dialect, Native(new ArrayType(new ArrayType(Integer, true), true)));
        Reject(dialect, Native(new ArrayType(new JsonType(), true)));
    }

    [TestCase("array", "INT2[]", DbType.Int16)]
    [TestCase("array", "FLOAT8[]", DbType.Double)]
    [TestCase("array", "STRING[]", DbType.String)]
    public void CockroachDecodesCanonicalArrayElementTypes(string family, string declaration, DbType expected)
        => Assert.That(CockroachStructuredTypes.Decode(family, declaration)?.Type?.Shape,
            Is.EqualTo(new ArrayType(new ScalarType(expected), true)));

    [TestCase("array", "VARCHAR(10)[]")]
    [TestCase("array", "JSONB[]")]
    [TestCase("vector", "VECTOR")]
    [TestCase("vector", "VECTOR(16001)")]
    public void CockroachReportsUnrepresentableDeclarations(string family, string declaration)
        => Assert.That(CockroachStructuredTypes.Decode(family, declaration)?.Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));

    [Test]
    public void CratePlansStrictObjectsNestedArraysAndQualifiedVectors()
    {
        var dialect = Dialect<CrateDbDialect>();
        var structure = new StructType([new("a\"b", new ArrayType(Integer, true), true)]);
        Assert.That(Plan(dialect, Native(new ArrayType(structure, true))),
            Does.Contain("""ARRAY(OBJECT(STRICT) AS ("a""b" ARRAY(INTEGER)))"""));
        var vector = Native(new VectorType(DbType.Single, 2048));
        Assert.That(Plan(dialect.WithServerVersion(new(5, 5)), vector), Does.Contain(" FLOAT_VECTOR(2048)"));
        Reject(dialect, vector);
        Reject(dialect.WithServerVersion(new(5, 5)), Native(new VectorType(DbType.Single, 2049)));
        Reject(dialect, Native(new StructType([new("required", Integer, false)])));
        Reject(dialect, Native(new ArrayType(new ScalarType(DbType.String), true)));
        Reject(dialect, new(new JsonType(), StructuredStorage.Json));
        Reject(dialect, Native(new MapType(Integer, Integer, true)));
    }

    [Test]
    public void QuestPlansOnlyNullableSingleRankDoubleArrays()
    {
        var dialect = Dialect<QuestDbDialect>();
        var supported = Native(new ArrayType(DoubleLeaf, true));
        var qualified = dialect.WithServerVersion(new(9, 0));
        Assert.That(Plan(qualified, supported), Does.Contain(" DOUBLE[]"));
        Reject(dialect, supported);
        Reject(dialect.WithServerVersion(new(8, 3)), supported);
        Reject(qualified, Native(new ArrayType(DoubleLeaf, false)));
        Reject(qualified, Native(new ArrayType(Integer, true)));
        Reject(qualified, Native(new ArrayType(new ArrayType(DoubleLeaf, true), true)));
        Reject(qualified, Native(new VectorType(DbType.Double, 3)));
        Assert.That(() => Plan(qualified, supported, required: true), Throws.TypeOf<NotSupportedException>());
    }

    internal static T Dialect<T>() where T : IDialect
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<T>(["structured"]);
        return builder.Build().Get<T>();
    }

    private static string Plan(IDialect dialect, StructuredColumnType type, bool required = false)
    {
        var column = new Column("value", type, constraints: required ? [new NotNullableConstraint()] : []);
        return new SchemaScriptRenderer(dialect).Plan(new DubUrl.Schema.Schema([new Table("t", [column], [])])).Single().CommandText;
    }

    private static void Reject(IDialect dialect, StructuredColumnType type)
        => Assert.That(() => Plan(dialect, type), Throws.TypeOf<NotSupportedException>());
}
