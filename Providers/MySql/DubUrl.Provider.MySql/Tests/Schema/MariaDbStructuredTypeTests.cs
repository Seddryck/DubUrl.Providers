using System.Data;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.MySql.Tests.Schema;

public class MariaDbStructuredTypeTests
{
    [TestCase(10, 5, 9, false)]
    [TestCase(10, 5, 10, true)]
    [TestCase(11, 8, 0, true)]
    public void JsonTextRequiresQualifiedMariaDb(int major, int minor, int patch, bool supported)
    {
        var dialect = Dialect().WithMariaDbDefaults(new(major, minor, patch));
        var json = new StructuredColumnType(new JsonType(new StructType([new("id", new ScalarType(DbType.Int32), false)])),
            StructuredStorage.JsonText);
        if (supported)
            Assert.That(Plan(dialect, json), Does.Contain(" JSON"));
        else
            Assert.That(() => Plan(dialect, json), Throws.TypeOf<NotSupportedException>());
    }

    [TestCase(11, 7, 0, DbType.Single, 3, false)]
    [TestCase(11, 7, 1, DbType.Single, 3, true)]
    [TestCase(11, 8, 0, DbType.Single, 16383, true)]
    [TestCase(11, 8, 0, DbType.Single, 16384, false)]
    [TestCase(11, 8, 0, DbType.Double, 3, false)]
    [TestCase(11, 8, 0, DbType.Int32, 3, false)]
    public void VectorQualifiesVersionPrecisionAndDimensions(int major, int minor, int patch, DbType component, int dimensions, bool supported)
    {
        var dialect = Dialect().WithMariaDbDefaults(new(major, minor, patch));
        var vector = new StructuredColumnType(new VectorType(component, dimensions), StructuredStorage.Native);
        if (supported)
            Assert.That(Plan(dialect, vector), Does.Contain(FormattableString.Invariant($" VECTOR({dimensions})")));
        else
            Assert.That(() => Plan(dialect, vector), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void CollectionsAndUnqualifiedRepresentationsAreRejected()
    {
        var dialect = Dialect();
        var mariaDb = dialect.WithMariaDbDefaults(new(11, 8));
        var mySql = dialect.WithInnoDbDefaults(new(8, 4));
        var integer = new ScalarType(DbType.Int32);
        var jsonText = new StructuredColumnType(new JsonType(), StructuredStorage.JsonText);
        var vector = new StructuredColumnType(new VectorType(DbType.Single, 3), StructuredStorage.Native);
        foreach (var shape in new StructuredType[] { new ArrayType(integer, true), new MapType(integer, integer, true),
            new StructType([new("item", integer, true)]) })
            Assert.That(() => Plan(mariaDb, new(shape, StructuredStorage.Native)), Throws.TypeOf<NotSupportedException>());
        foreach (var unqualified in new[] { dialect, mySql })
        {
            Assert.That(() => Plan(unqualified, jsonText), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => Plan(unqualified, vector), Throws.TypeOf<NotSupportedException>());
        }
    }

    internal static MySqlDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MySqlDialect>(["mariadb"]);
        return builder.Build().Get<MySqlDialect>();
    }

    private static string Plan(IDialect dialect, StructuredColumnType type)
        => new SchemaScriptRenderer(dialect).Plan(new DubUrl.Schema.Schema([new Table("t", [new Column("value", type)], [])]))
            .Single().CommandText;
}
