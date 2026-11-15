using System.Data;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.SqlServer.Tests.Schema;

public sealed class SqlServerColumnAnnotationTests
{
    [Test]
    public void PlansColumnScopedPropertiesAndCommentsInTheTargetDatabase()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssql"]);
        var column = new Column("item code", DbType.Int32, description: "Identifier")
        { Annotations = new Dictionary<string, string> { ["myapp.owner"] = "team's données" } };
        var table = new Table(new DatabaseObjectName("items", "sales", "warehouse"), [column]);

        var plan = new SchemaScriptRenderer(builder.Build().Get<TSqlDialect>()).Plan(new([table]));
        Assert.That(plan[1].CommandText, Does.Contain("EXEC [warehouse].sys.sp_executesql")
            .And.Contain("myapp.owner").And.Contain("team''s données").And.Contain("@columnName = N'item code'"));
        Assert.That(string.Join("\n", plan.Select(command => command.CommandText)), Does.Contain("MS_Description"));
    }
}
