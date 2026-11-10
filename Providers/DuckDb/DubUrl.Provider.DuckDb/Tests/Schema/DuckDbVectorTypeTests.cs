using System.Data;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.DuckDb.Tests.Schema;

public sealed class DuckDbVectorTypeTests
{
    [TestCase(DbType.Single, "FLOAT", 3)]
    [TestCase(DbType.Double, "DOUBLE", 1)]
    [TestCase(DbType.Int64, "BIGINT", 100_000)]
    [TestCase(DbType.UInt64, "UBIGINT", 3)]
    public void NativeVectorsUseFixedArraysAndNonNullComponents(DbType component, string element, int dimensions)
    {
        var vector = new VectorType(component, dimensions);
        var declaration = FormattableString.Invariant($"{element}[{dimensions}]");
        Assert.That(Plan(new(vector, StructuredStorage.Native)), Does.Contain(declaration + " CHECK")
            .And.Contain("system.main.list_count(\"value\"\"column\")"));
        Assert.That(DuckDbCatalogSql.FixedVector(declaration), Is.EqualTo(vector));
    }

    [TestCase(DbType.Single, 100_001, StructuredStorage.Native)]
    [TestCase(DbType.Decimal, 3, StructuredStorage.Native)]
    [TestCase(DbType.Single, 3, StructuredStorage.Binary)]
    public void RejectsUnsupportedVectorRepresentations(DbType component, int dimensions, StructuredStorage storage)
        => Assert.That(() => Plan(new(new VectorType(component, dimensions), storage)), Throws.TypeOf<NotSupportedException>());

    [TestCase("FLOAT[3][]")]
    [TestCase("FLOAT[3][3]")]
    [TestCase("FLOAT[100001]")]
    [TestCase("FLOAT[0]")]
    [TestCase("DECIMAL(5,2)[3]")]
    [TestCase("VARCHAR[3]")]
    public void OnlySupportedTopLevelFixedNumericArraysAreCandidates(string declaration)
        => Assert.That(DuckDbCatalogSql.FixedVector(declaration), Is.Null);

    [TestCase("""("system".main.list_count("value""column") = 3)""", true)]
    [TestCase("""((system.main.list_count("value""column") = 3))""", true)]
    [TestCase("""(list_count("value""column") = 3)""", false)]
    [TestCase("""(system.main.list_count("other") = 3)""", false)]
    [TestCase("""(system.main.list_count("value""column") = 2)""", false)]
    [TestCase("""(system.main.list_count("value""column") = 3 OR true)""", false)]
    public void RequiresTheQualifiedCheckForTheSameColumnAndDimensions(string expression, bool expected)
        => Assert.That(DuckDbCatalogSql.IsVectorCheck(expression, "value\"column", 3), Is.EqualTo(expected));

    private static string Plan(StructuredColumnType type)
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        var schema = new DubUrl.Schema.Schema([new Table("items", [new Column("value\"column", type)], [])]);
        return new SchemaScriptRenderer(builder.Build().Get<DuckDbDialect>()).Plan(schema).Single().CommandText;
    }
}
