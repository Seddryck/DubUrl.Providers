using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.Mapping;
using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public class PostgreSqlArrayTests
{
    private static readonly ScalarType Integer = new(DbType.Int32);

    [TestCase(typeof(PostgresqlMapper))]
    [TestCase(typeof(TimescaleMapper))]
    public void RoutesRegisterPostgreSqlDiscovery(Type mapper)
        => Assert.That(((SchemaDiscoveryAttribute)Attribute.GetCustomAttribute(mapper, typeof(SchemaDiscoveryAttribute))!).Create().Reader,
            Is.TypeOf<PostgreSqlMetadataReader>());

    [Test]
    public void RenderUsesQualifiedElementTypeAndQuotesTheRankCheckIdentifier()
    {
        var schema = new DubUrl.Schema.Schema([new Table("sample",
            [new Column("a\"b", new StructuredColumnType(new ArrayType(Integer, true), StructuredStorage.Native))], [])]);
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["pg"]);
        var statement = new SchemaScriptRenderer(builder.Build().Get<PgsqlDialect>()).Plan(schema).Single().CommandText;
        Assert.That(statement, Does.Contain("\"a\"\"b\" pg_catalog.int4[] "));
        Assert.That(statement, Does.Contain("""CHECK (pg_catalog.array_ndims("a""b") OPERATOR(pg_catalog.=) 1)"""));
    }

    [Test]
    public void RejectsUnsupportedElementShapesAndNullability()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["pg"]);
        var renderer = new SchemaScriptRenderer(builder.Build().Get<PgsqlDialect>());
        foreach (var array in new[] { new ArrayType(Integer, false), new ArrayType(Integer, null),
            new ArrayType(new ArrayType(Integer, true), true), new ArrayType(new ScalarType(DbType.Decimal), true),
            new ArrayType(new JsonType(new StructType([new("id", Integer, false)])), true) })
        {
            var schema = new DubUrl.Schema.Schema([new Table("sample",
                [new Column("items", new StructuredColumnType(array, StructuredStorage.Native))], [])]);
            Assert.That(() => renderer.Plan(schema), Throws.TypeOf<NotSupportedException>());
        }
    }

    [TestCase(2, -1, "pg_catalog", "int4", true)]
    [TestCase(1, 14, "pg_catalog", "varchar", true)]
    [TestCase(1, -1, "public", "int4", true)]
    [TestCase(1, -1, "pg_catalog", "text", false)]
    [TestCase(1, -1, null, null, true)]
    public void UnsupportedCatalogShapesAreNotFlattened(int rank, int modifier, string? schema, string? element, bool defaultCollation)
    {
        var result = PostgreSqlArrayType.Decode(rank, modifier, schema, element, defaultCollation, true);
        Assert.That(result.Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(result.Type, Is.Null);
    }

    [TestCase(true, MetadataCoverage.Available)]
    [TestCase(false, MetadataCoverage.Unrepresentable)]
    public void RankCheckDeterminesCoverage(bool checkedRank, MetadataCoverage expected)
    {
        var result = PostgreSqlArrayType.Decode(1, -1, "pg_catalog", "int4", true, checkedRank);
        Assert.That(result.Coverage, Is.EqualTo(expected));
        Assert.That(result.Type, Is.EqualTo(new StructuredColumnType(new ArrayType(Integer, true), StructuredStorage.Native)));
    }
}
