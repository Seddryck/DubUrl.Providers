using System.Data;
using DubUrl.Provider.Sqlite.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Provider.Sqlite.Tests.Schema;

public class SqliteStructuredTypeTests
{
    [TestCase(StructuredStorage.JsonText, 37, false)]
    [TestCase(StructuredStorage.JsonText, 38, true)]
    [TestCase(StructuredStorage.Binary, 44, false)]
    [TestCase(StructuredStorage.Binary, 45, true)]
    [TestCase(StructuredStorage.Json, 45, false)]
    [TestCase(StructuredStorage.Native, 45, false)]
    public void JsonRequiresQualifiedStorage(StructuredStorage storage, int minor, bool supported)
    {
        var renderer = Renderer(new(3, minor));
        var shape = new JsonType(new StructType([new("id", new ScalarType(DbType.Int32), false)]));
        var schema = Model("main", new(shape, storage));
        if (supported)
            Assert.That(renderer.Plan(schema).Single().CommandText,
                Does.Contain(storage == StructuredStorage.Binary ? " BLOB CHECK" : " TEXT CHECK"));
        else
            Assert.That(() => renderer.Plan(schema), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void NativeCollectionsAndUnqualifiedJsonAreRejected()
    {
        var integer = new ScalarType(DbType.Int32);
        foreach (var type in new StructuredType[] { new ArrayType(integer, true), new MapType(integer, integer, true),
            new StructType([new("id", integer, true)]), new VectorType(DbType.Single, 3) })
            Assert.That(() => Renderer(new(3, 45)).Plan(Model("main", new(type, StructuredStorage.Native))),
                Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Renderer(null).Plan(Model("main", new(new JsonType(), StructuredStorage.JsonText))),
            Throws.TypeOf<NotSupportedException>());
    }

    private static DubUrl.Schema.Schema Model(string database, StructuredColumnType type)
        => new([new Table(new DatabaseObjectName("items", database: database),
            [new Column("id", DbType.Int64), new Column("payload\"json", type, constraints: [new NullableConstraint()])], [])]);

    private static SchemaScriptRenderer Renderer(Version? version)
    {
        var registry = new DialectRegistryBuilder();
        registry.AddDialect<SqliteDialect>(["sqlite"]);
        var dialect = registry.Build().Get<SqliteDialect>();
        return new(version is null ? dialect : dialect.WithServerVersion(version));
    }
}
