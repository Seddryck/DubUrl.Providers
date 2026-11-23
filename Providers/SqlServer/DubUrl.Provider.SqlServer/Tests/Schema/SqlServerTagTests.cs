using System.Data;
using DubUrl.Provider.SqlServer.Schema.Generation;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.SqlServer.Tests.Schema;

public sealed class SqlServerTagTests
{
    [Test]
    public void RendersTagPropertiesAtTheCorrectScopeWithoutUsingAnnotationNames()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssql"]);
        var table = new Table(new DatabaseObjectName("items", "sales"),
            [new Column("item code", DbType.Int32) { Tags = new Dictionary<string, string> { ["PII"] = "team's data" } }],
            null, null, null, new Dictionary<string, string> { ["pii"] = "annotation" })
        { Tags = new Dictionary<string, string> { ["pii"] = "true" } };

        var dialect = builder.Build().Get<TSqlDialect>();
        var renderer = new SchemaScriptRenderer(dialect);
        var sql = renderer.Render(new([table]));
        var tableTag = ((DubUrl.Schema.Renderers.ITableTagSqlRenderer)dialect.SchemaSqlRenderer)
            .PlanTableTags(dialect, table.Identity, table.Tags).Single();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("@propertyName = N'pii'").And.Contain("annotation"));
            Assert.That(tableTag.CommandText, Does.Contain("dbu.tag.v1.706969").And.Contain("N'true'").And.Not.Contain("@columnName"));
            Assert.That(sql, Does.Contain("dbu.tag.v1.504949").And.Contain("team''s data")
                .And.Contain("@columnName = N'item code'"));
        });
    }

    [Test]
    public void LimitsEncodedTagNamesToTheSqlServerPropertyNameLength()
    {
        Assert.That(SqlServerTags.Encode(new string('x', 58)), Has.Length.EqualTo(127));
        Assert.That(() => SqlServerTags.Encode(new string('é', 30)), Throws.TypeOf<NotSupportedException>());
    }
}
