using System.Data;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.Tests.Schema;

public sealed class PostgreSqlGeneratedColumnTests
{
    [TestCase(null, false)]
    [TestCase(17, false)]
    [TestCase(18, true)]
    public void VirtualGenerationRequiresPostgreSql18(int? serverMajor, bool supported)
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["pgsql"]);
        var unqualified = builder.Build().Get<PgsqlDialect>();
        var dialect = serverMajor is int major ? unqualified.WithServerVersion(new Version(major, 0)) : unqualified;
        var renderer = new SchemaScriptRenderer(dialect);

        Assert.That(dialect.Capabilities.SupportsVirtualGeneratedColumns, Is.EqualTo(supported));
        Assert.That(unqualified.Capabilities.SupportsVirtualGeneratedColumns, Is.False);
        Assert.That(renderer.Render(Model(GeneratedColumnStorage.Stored)), Does.Contain("GENERATED ALWAYS AS (\"source\") STORED"));

        var virtualColumn = Model(GeneratedColumnStorage.Virtual);
        if (supported)
            Assert.That(renderer.Render(virtualColumn), Does.Contain("GENERATED ALWAYS AS (\"source\") VIRTUAL"));
        else
            Assert.That(() => renderer.Plan(virtualColumn), Throws.TypeOf<NotSupportedException>().With.Message.Contains("PostgreSQL 18"));
    }

    private static DubUrl.Schema.Schema Model(GeneratedColumnStorage storage)
        => new([new Table("generated_values", [
            new Column("source", DbType.Int32),
            new Column("copy", DbType.Int32, generation: new GeneratedColumnGeneration(new ColumnIdentityExpression("source"), storage)),
        ])]);
}
