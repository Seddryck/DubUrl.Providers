using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Provider.MySql.Schema.Generation;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public class StructuredColumnTypeTests
{
    private static readonly ScalarType Integer = new(DbType.Int32);

    [Test]
    public void JsonUsesTheExplicitRepresentation()
    {
        var shape = new JsonType(new StructType([new("items", new ArrayType(Integer, true), false)]));
        Assert.That(Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Json)), Does.Contain(" JSON"));
        Assert.That(Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.JsonText)), Does.Contain(" VARCHAR"));
        Assert.That(Plan(Dialect<PgsqlDialect>(), new(shape, StructuredStorage.Json)), Does.Contain(" JSONB"));
        Assert.That(Plan(Dialect<MySqlDialect>().WithInnoDbDefaults(new(8, 4)), new(shape, StructuredStorage.Json)), Does.Contain(" JSON"));
        Assert.That(() => Plan(Dialect<MySqlDialect>().WithMariaDbDefaults(new(11, 4)), new(shape, StructuredStorage.Json)),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Binary)),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void DuckDbQuotesFieldNamesAndPreservesNesting()
    {
        var fieldName = "select\"), DROP TABLE t";
        var shape = new StructType([new(fieldName, new MapType(Integer, new ArrayType(Integer, true), true), true)]);
        var sqlType = """STRUCT("select""), DROP TABLE t" MAP(INTEGER, INTEGER[]))""";
        Assert.That(Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Native)), Does.Contain(sqlType));
        Assert.That(DuckDbCatalogSql.StructuredType(sqlType)?.Type?.Shape, Is.EqualTo(shape));
    }

    private static IEnumerable<StructuredType> InvalidDuckDbShapes()
    {
        yield return new ArrayType(new ArrayType(Integer, false), true);
        yield return new ArrayType(Integer, null);
        yield return new MapType(new ArrayType(Integer, true), Integer, true);
        yield return new MapType(Integer, Integer, false);
        yield return new StructType([new("Key", Integer, true), new("key", Integer, true)]);
        yield return new StructType([new("field", Integer, false)]);
        yield return new ArrayType(new ScalarType(DbType.Decimal), true);
    }

    [TestCaseSource(nameof(InvalidDuckDbShapes))]
    public void DuckDbRejectsUnsupportedNestedDefinitions(StructuredType shape)
        => Assert.That(() => Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());

    [TestCase("FLOAT[3]")]
    [TestCase("DECIMAL(6,2)[]")]
    [TestCase("STRUCT(a INTEGER, A VARCHAR)")]
    [TestCase("MAP(INTEGER[], INTEGER)")]
    [TestCase("UNION(a INTEGER, b VARCHAR)")]
    public void DuckDbReportsUnrepresentableCatalogTypes(string name)
    {
        var metadata = DuckDbCatalogSql.StructuredType(name);
        Assert.That(metadata?.Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(metadata?.Type, Is.Null);
    }

    [Test]
    public void SingleStoreValidatesDimensionsAndComponentTypes()
    {
        var dialect = Dialect<SingleStoreDialect>().WithServerVersion(new(9, 1));
        Assert.That(Plan(dialect, new(new VectorType(DbType.Single, 3), StructuredStorage.Native)),
            Does.Contain("VECTOR(3, F32)"));
        Assert.That(SingleStoreVectorType.Decode("vector(3, F32)").Type?.Shape, Is.EqualTo(new VectorType(DbType.Single, 3)));
        Assert.That(SingleStoreVectorType.Decode("vector(3, F16)").Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(() => new VectorType(DbType.Single, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => Plan(dialect, new(new VectorType(DbType.Single, int.MaxValue), StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(dialect, new(new VectorType(DbType.Decimal, 3), StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());
    }

    private static string Plan(IDialect dialect, StructuredColumnType type)
        => string.Join("\n", new SchemaScriptRenderer(dialect).Plan(new(
            [new Table("structured_values", [new Column("v", type)], [])])).Select(command => command.CommandText));

    private static TDialect Dialect<TDialect>() where TDialect : IDialect
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TDialect>(["test"]);
        return builder.Build().Get<TDialect>();
    }
}
