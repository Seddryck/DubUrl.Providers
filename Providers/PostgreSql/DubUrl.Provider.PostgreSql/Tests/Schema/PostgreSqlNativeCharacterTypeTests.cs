using System.Data;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public class PostgreSqlNativeCharacterTypeTests
{
    private static PgsqlDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["pgsql"]);
        return builder.Build().Get<PgsqlDialect>();
    }

    [TestCase("char", "CHAR")]
    [TestCase("Character", "CHAR")]
    [TestCase("varchar", "VARCHAR")]
    [TestCase("character varying", "VARCHAR")]
    public void NativeAliasesRenderKeyLengthOnce(string name, string expected)
    {
        var nativeType = new NativeDatabaseType(name, typeof(PgsqlDialect));
        var column = new VarLengthColumn("code", DbType.String, 12,
            constraints: [new PrimaryKeyConstraint()], nativeType: nativeType);
        var schema = new DubUrl.Schema.Schema([new Table("items", [column])], []);
        var sql = new SchemaScriptRenderer(CreateDialect()).Render(schema);

        Assert.That(sql, Does.Contain($"\"code\" {expected}(12)"));
        Assert.That(sql, Does.Not.Contain("(12)(12)"));
    }

    [TestCase("CHAR", null, "CHAR")]
    [TestCase("CHARACTER", 1, "CHAR(1)")]
    [TestCase("CHAR", 10_485_760, "CHAR(10485760)")]
    [TestCase("VARCHAR", 10_485_760, "VARCHAR(10485760)")]
    [TestCase("VARCHAR", null, "VARCHAR")]
    [TestCase("CHARACTER VARYING", -1, "VARCHAR")]
    public void NativeCharacterTypesAcceptValidDimensions(string type, int? length, string expected)
    {
        var dialect = CreateDialect();
        Assert.That(dialect.SchemaSqlRenderer.RenderColumnType(dialect, type, length, 0, 0, true), Is.EqualTo(expected));
    }

    [TestCase("CHAR", 0, 0, 0)]
    [TestCase("CHARACTER", -1, 0, 0)]
    [TestCase("VARCHAR", -2, 0, 0)]
    [TestCase("CHAR", 10_485_761, 0, 0)]
    [TestCase("CHARACTER VARYING", 10_485_761, 0, 0)]
    [TestCase("VARCHAR", 12, 2, 0)]
    [TestCase("CHAR", 12, 0, 1)]
    public void NativeCharacterTypesRejectInvalidDimensions(string type, int length, int precision, int scale)
    {
        var dialect = CreateDialect();
        Assert.That(() => dialect.SchemaSqlRenderer.RenderColumnType(dialect, type, length, precision, scale, true),
            Throws.ArgumentException);
    }

    [TestCase("VARCHAR(12)")]
    [TestCase("CHAR; DROP TABLE items")]
    public void NativeTypeNamesRejectEmbeddedDimensionsAndSql(string name)
        => Assert.That(() => new NativeDatabaseType(name), Throws.ArgumentException);

    [TestCase("NVARCHAR")]
    [TestCase("VARCHAR2")]
    public void UnregisteredNativeAliasesAreRejected(string name)
    {
        var dialect = CreateDialect();
        Assert.That(NativeTypeRegistry.Default.TryResolve(dialect, new NativeDatabaseType(name), out _), Is.False);
    }
}
