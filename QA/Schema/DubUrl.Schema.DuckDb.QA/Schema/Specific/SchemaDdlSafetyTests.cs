using System.Data;
using DuckDB.NET.Data;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using Constraint = DubUrl.Schema.Constraints.Constraint;
using ForeignKey = DubUrl.Schema.Constraints.ForeignKeyConstraint;
using SchemaModel = DubUrl.Schema.Schema;

namespace DubUrl.Provider.DuckDb.QA.Schema.Specific;

[Category("Schema"), NonParallelizable]
public sealed class SchemaDdlSafetyTests
{
    private static IDialect Dialect<T>()
        where T : IDialect
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<T>([typeof(T).Name]);
        return builder.Build().Get<T>();
    }

    private static void Execute(IDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(IDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SchemaModel CheckedSchema(bool tableChecks)
    {
        Constraint[] checks = [
            new CheckConstraint(new ColumnIdentityExpression("age"), ">=", new ValueExpression(0)),
            new MembershipCheckConstraint(new ColumnIdentityExpression("category"), ["STD", "PRE"]),
            new MembershipCheckConstraint(new ColumnIdentityExpression("optional"), ["STD", null], NullMembershipBehavior.MatchNull),
            new MembershipCheckConstraint(new ColumnIdentityExpression("null_only"), [null], NullMembershipBehavior.MatchNull),
            new RegexCheckConstraint(new ColumnIdentityExpression("code"), "^[A-Z]+$"),
            new RegexCheckConstraint(new ColumnIdentityExpression("required_code"), "^[A-Z]+$", RegexNullBehavior.RejectNull)
        ];
        var names = new[] { "age", "category", "optional", "null_only", "code", "required_code" };
        var columns = names.Select((name, i) => new Column(name, i == 0 ? DbType.Int32 : DbType.String,
            constraints: tableChecks ? [] : [checks[i]])).ToArray();
        return new SchemaModel([new Table("checks", columns, tableChecks ? checks : [])]);
    }


    private static Table Parent(Column? id = null) => new("parent", [id ?? new Column("id", DbType.Int32, constraints: [new PrimaryKeyConstraint()])]);
    private static Table Child(Column? id = null, string target = "parent") => new("child", [id ?? new Column("id", DbType.Int32)],
        [new ForeignKey("fk_child_parent", ["id"], target, ["id"])]);


    [TestCase(false)]
    [TestCase(true)]
    [Category("DuckDbIntegration")]
    public void CheckPredicates_DuckDbExecutesAndEnforcesAllForms(bool tableChecks)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        Execute(connection, new SchemaScriptRenderer(Dialect<DuckDbDialect>()).Render(CheckedSchema(tableChecks)));
        Execute(connection, "INSERT INTO checks VALUES (0, 'STD', NULL, NULL, NULL, 'ABC')");
        var invalidRows = new[] {
            "(-1, 'STD', NULL, NULL, NULL, 'ABC')", "(0, 'OTHER', NULL, NULL, NULL, 'ABC')",
            "(0, 'STD', 'OTHER', NULL, NULL, 'ABC')", "(0, 'STD', NULL, 'x', NULL, 'ABC')",
            "(0, 'STD', NULL, NULL, 'lower', 'ABC')", "(0, 'STD', NULL, NULL, NULL, NULL)"
        };
        foreach (var row in invalidRows)
            Assert.That(() => Execute(connection, "INSERT INTO checks VALUES " + row), Throws.Exception, row);
    }

    [Test]
    [Category("DuckDbIntegration")]
    public void DuckDb_ReversedDependenciesExecuteAndDropInReverseOrder()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        var schema = new SchemaModel([Child(), Parent()]);
        var renderer = new SchemaScriptRenderer(Dialect<DuckDbDialect>(), SchemaCreationOptions.DropIfExists);
        var script = renderer.Render(schema);
        Assert.That(script.IndexOf("CREATE TABLE parent", StringComparison.Ordinal), Is.LessThan(script.IndexOf("CREATE TABLE child", StringComparison.Ordinal)));
        Assert.That(script, Does.Not.Contain("ALTER TABLE"));
        Execute(connection, script);
        Execute(connection, "INSERT INTO parent VALUES (1); INSERT INTO child VALUES (1)");
        Assert.That(() => Execute(connection, "INSERT INTO child VALUES (2)"), Throws.Exception);
        Execute(connection, script);
    }

    [Test]
    [Category("DuckDbIntegration")]
    public void DuckDb_ExternalParentIsValidatedButNeverCreatedOrDropped()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE parent(id INTEGER PRIMARY KEY); INSERT INTO parent VALUES (1)");
        var script = new SchemaScriptRenderer(Dialect<DuckDbDialect>(), SchemaCreationOptions.DropIfExists)
            .Render(new SchemaModel([Child()], [], [], [Parent()]));
        Assert.That(script, Does.Not.Contain("CREATE TABLE parent").And.Not.Contain("DROP TABLE IF EXISTS parent"));
        Execute(connection, script);
        Execute(connection, "INSERT INTO child VALUES (1)");
        Assert.That(() => Execute(connection, "INSERT INTO child VALUES (2)"), Throws.Exception);
    }

    [Test]
    [Category("DuckDbIntegration")]
    public void DuckDb_SelfReferenceExecutesForExistingParentRows()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        var table = new Table("node", [new Column("id", DbType.Int32, constraints: [new PrimaryKeyConstraint()]), new Column("parent", DbType.Int32)],
            [new ForeignKey("fk_node", ["parent"], "node", ["id"])]);
        Execute(connection, new SchemaScriptRenderer(Dialect<DuckDbDialect>()).Render(new SchemaModel([table])));
        Execute(connection, "INSERT INTO node VALUES (1, NULL); INSERT INTO node VALUES (2, 1)");
        Assert.That(() => Execute(connection, "INSERT INTO node VALUES (3, 99)"), Throws.Exception);
    }
}
