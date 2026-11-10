using System.Data;
using DubUrl.Provider.PostgreSql.Schema.Generation;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public sealed class PostgreSqlCompositeTypeTests
{
    private static readonly ScalarType Integer = new(DbType.Int32);
    private static readonly DatabaseObjectName InnerName = new("inner\"type", "audit\"schema");
    private static readonly DatabaseObjectName OuterName = new("outer", "public");
    private static readonly StructType Inner = new([new("x\"key", Integer, true)]);
    private static readonly StructType Outer = new([new("inner", Inner, true), new("doc", new JsonType(), true)]);

    [Test]
    public void RegisteredCompositeDefinitionsAndTableDdlUseQualifiedNames()
    {
        var original = Dialect();
        var qualified = original.WithCompositeType(InnerName, Inner).WithCompositeType(OuterName, Outer);
        Assert.That(original.RenderCompositeTypeDefinitions(), Is.Empty);
        Assert.That(qualified.RenderCompositeTypeDefinitions(), Is.EqualTo(new[]
        {
            """CREATE TYPE "audit""schema"."inner""type" AS ("x""key" pg_catalog.int4);""",
            """CREATE TYPE "public"."outer" AS ("inner" "audit""schema"."inner""type", "doc" pg_catalog.jsonb);"""
        }));

        var renderer = new SchemaScriptRenderer(qualified);
        var schema = Model(Outer);
        const string reference = "\"value\" \"public\".\"outer\"";
        Assert.That(renderer.Plan(schema).Single().CommandText, Does.Contain(reference));
        Assert.That(renderer.Render(schema), Does.Contain(reference));
        Assert.That(() => new SchemaScriptRenderer(original).Plan(schema), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void RejectsAmbiguousBindingsAndUnregisteredDependencies()
    {
        var dialect = Dialect();
        Assert.That(() => dialect.WithCompositeType(OuterName, Outer), Throws.TypeOf<NotSupportedException>());
        var qualified = dialect.WithCompositeType(InnerName, Inner);
        Assert.That(qualified.WithCompositeType(InnerName, Inner).RenderCompositeTypeDefinitions(), Has.Count.EqualTo(1));
        Assert.That(() => qualified.WithCompositeType(OuterName, Inner), Throws.ArgumentException);
        Assert.That(() => qualified.WithCompositeType(InnerName, Outer), Throws.ArgumentException);
    }

    [Test]
    public void RejectsFieldsWithoutReversiblePostgreSqlStorage()
    {
        var dialect = Dialect();
        foreach (var field in new StructField[] { new("required", Integer, false), new("unknown", Integer, null),
            new("bounded", new ScalarType(DbType.Decimal), true), new("list", new ArrayType(Integer, true), true) })
            Assert.That(() => dialect.WithCompositeType(OuterName, new([field])), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.WithCompositeType(new("unqualified"), Inner), Throws.ArgumentException);
        Assert.That(() => dialect.WithCompositeType(OuterName, new([new(new string('é', 32), Integer, true)])), Throws.ArgumentException);
    }

    [Test]
    public void ProvisionedCompositesStayWithinTheCatalogDepthLimit()
    {
        var dialect = Dialect().WithCompositeType(new("level_1", "public"), Inner);
        var shape = Inner;
        for (var depth = 2; depth <= PostgreSqlCompositeType.MaxDepth; depth++)
        {
            shape = new([new("nested", shape, true)]);
            dialect = dialect.WithCompositeType(new($"level_{depth}", "public"), shape);
        }
        Assert.That(dialect.RenderCompositeTypeDefinitions(), Has.Count.EqualTo(PostgreSqlCompositeType.MaxDepth));
        Assert.That(() => dialect.WithCompositeType(new("too_deep", "public"), new([new("nested", shape, true)])),
            Throws.TypeOf<NotSupportedException>());
    }

    internal static PgsqlDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["pg"]);
        return builder.Build().Get<PgsqlDialect>();
    }

    private static DubUrl.Schema.Schema Model(StructType shape)
        => new([new Table("items", [new Column("value", new StructuredColumnType(shape, StructuredStorage.Native))], [])]);
}
