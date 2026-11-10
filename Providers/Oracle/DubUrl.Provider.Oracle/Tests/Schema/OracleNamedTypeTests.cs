using System.Data;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Oracle.Tests.Schema;

public sealed class OracleNamedTypeTests
{
    private static readonly ScalarType Integer = new(DbType.Int32);
    private static readonly ArrayType Items = new(Integer, true);
    private static readonly StructType Payload = new([new("ordered values", Items, true), new("count", Integer, true)]);

    [Test]
    public void RegistersAndRendersDependenciesBeforeObjectTypes()
    {
        var original = Dialect();
        var dialect = original.WithVarrayType(new("ordered_values", "app"), Items, 10)
            .WithObjectType(new("payload", "app"), Payload);
        Assert.That(original.RenderNamedTypeDefinitions(), Is.Empty);
        Assert.That(dialect.RenderNamedTypeDefinitions(), Is.EqualTo(new[]
        {
            """CREATE TYPE "app"."ordered_values" AS VARRAY(10) OF NUMBER(10, 0)""",
            """CREATE TYPE "app"."payload" AS OBJECT ("ordered values" "app"."ordered_values", "count" NUMBER(10, 0))"""
        }));
        var schema = new DubUrl.Schema.Schema([new Table("items",
            [new Column("value", new StructuredColumnType(Payload, StructuredStorage.Native))], [])]);
        var renderer = new SchemaScriptRenderer(dialect);
        Assert.That(renderer.Plan(schema).Single().CommandText, Does.Contain("\"app\".\"payload\""));
        Assert.That(renderer.Render(schema), Does.Contain("\"app\".\"payload\""));
        Assert.That(() => new SchemaScriptRenderer(original).Plan(schema), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void RejectsUnknownNullabilityUnsupportedMembersAndConflictingBindings()
    {
        var dialect = Dialect();
        var name = new DatabaseObjectName("values_t", "app");
        Assert.That(() => dialect.WithVarrayType(name, Items, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => dialect.WithVarrayType(name, new(Integer, null), 10), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.WithVarrayType(name, new(Integer, false), 10), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.WithObjectType(name, new([new("required", Integer, false)])), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.WithObjectType(name, new([new("doc", new JsonType(), true)])), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.WithObjectType(name, Payload), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.WithVarrayType(new("bad\"name", "app"), Items, 10), Throws.ArgumentException);
        Assert.That(() => dialect.WithVarrayType(name, Items, 10).WithVarrayType(name, Items, 11), Throws.ArgumentException);
    }

    [Test]
    public void LongerIdentifiersRequireTheServerAndCompatibilityBaseline()
    {
        var name = new DatabaseObjectName(new string('é', 16), "app");
        var dialect = Dialect().WithServerVersion(new(23, 0), new(12, 1));
        Assert.That(() => dialect.WithVarrayType(name, Items, 1), Throws.ArgumentException);
        Assert.That(dialect.WithServerVersion(new(23, 0), new(12, 2)).WithVarrayType(name, Items, 1).RenderNamedTypeDefinitions(),
            Has.Count.EqualTo(1));
    }

    private static OracleDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<OracleDialect>(["oracle"]);
        return builder.Build().Get<OracleDialect>();
    }
}
