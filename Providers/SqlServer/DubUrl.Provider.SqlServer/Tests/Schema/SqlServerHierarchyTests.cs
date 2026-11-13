using System.Data;
using DubUrl.Provider.SqlServer.Schema.Generation;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Builders;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public class SqlServerHierarchyTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void PreflightReportsTheNativeRepresentationAndActualEnforcement(bool enforce)
    {
        var identity = new DatabaseObjectName("nodes", "dbo");
        var schema = new DubUrl.Schema.Schema([NodeTable(identity, enforce)]);
        var renderer = new SchemaScriptRenderer(Dialect());

        var plan = renderer.PreflightHierarchies(schema).Single();
        var commands = renderer.Plan(schema);
        var ddl = commands.Single(command => command.Operation == SchemaSqlOperation.CreateTable).CommandText;
        var foreignKeys = commands.Where(command => command.Operation == SchemaSqlOperation.ForeignKey).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(plan, Is.EqualTo(new HierarchyPlan(identity, HierarchyRepresentation.HierarchyId, enforce, null)));
            Assert.That(ddl, Does.Contain("PRIMARY KEY ([id])").And.Contain("[node_path] HIERARCHYID NOT NULL").And.Contain("UNIQUE ([node_path])"));
            Assert.That(ddl, Does.Not.Contain("parent_id"));
            Assert.That(renderer.Render(schema), Does.Contain("DubUrl.Hierarchy"));
            if (enforce)
            {
                Assert.That(ddl, Does.Contain("[parent_path] AS ([node_path].GetAncestor(1)) PERSISTED"));
                Assert.That(foreignKeys.Single().CommandText,
                    Does.Contain("FOREIGN KEY ([parent_path]) REFERENCES [dbo].[nodes] ([node_path])"));
            }
            else
            {
                Assert.That(ddl, Does.Not.Contain("parent_path"));
                Assert.That(foreignKeys, Is.Empty);
            }
        });
    }

    [Test]
    public void PreflightRejectsAConflictingPathColumn()
    {
        var table = NodeTable(new("nodes"), true, "NODE_PATH");
        var schema = new DubUrl.Schema.Schema([table]);
        var renderer = new SchemaScriptRenderer(Dialect());

        var result = renderer.PreflightHierarchies(schema).Single();

        Assert.That(result.IsSupported, Is.False);
        Assert.That(result.Diagnostic, Does.Contain("already exists").And.Contain("node_path"));
        Assert.That(() => renderer.Plan(schema), Throws.TypeOf<NotSupportedException>().With.Message.EqualTo(result.Diagnostic));
    }

    [TestCase("([node_path].[GetAncestor]((1)))", "node_path")]
    [TestCase("(([quoted]].name].getancestor(1)))", "quoted].name")]
    public void CatalogDecoderRecognizesTheExactParentExpression(string sql, string column)
    {
        var result = SqlServerHierarchy.DecodeParentExpression(sql);
        Assert.That(result?.Arguments?.Single().Name, Is.EqualTo(column));
        Assert.That(SqlServerHierarchy.DecodeParentExpression(sql.Replace("1", "2", StringComparison.Ordinal)), Is.Null);
    }

    private static Table NodeTable(DatabaseObjectName name, bool enforce, string? extraColumn = null)
        => new TableBuilder().WithIdentity(name)
            .WithColumns(columns =>
            {
                columns.Add(column => column.WithName("id").WithType(DbType.Int32));
                return extraColumn is null ? columns : columns.Add(column => column.WithName(extraColumn).WithType(DbType.Int32));
            })
            .WithConstraints(constraints => constraints.AddPrimaryKey(key => key.WithColumnNames("id")))
            .WithHierarchy(new Hierarchy("id", enforce))
            .Build();

    private static TSqlDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssql"]);
        return builder.Build().Get<TSqlDialect>();
    }
}