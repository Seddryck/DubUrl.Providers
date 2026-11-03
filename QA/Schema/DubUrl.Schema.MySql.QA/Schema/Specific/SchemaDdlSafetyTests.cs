using System.Data;
using MySqlConnector;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using Constraint = DubUrl.Schema.Constraints.Constraint;
using ForeignKey = DubUrl.Schema.Constraints.ForeignKeyConstraint;
using SchemaModel = DubUrl.Schema.Schema;

namespace DubUrl.Provider.MySql.QA.Schema.Specific;

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


    [TestCase(null)]
    [TestCase("VARCHAR")]
    [TestCase("CHAR")]
    [Category("MySqlIntegration")]
    public void MySql_BoundedKeysChecksAndDescriptionsExecute(string? nativeType)
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_SCHEMA_MYSQL");
        if (string.IsNullOrEmpty(connectionString)) Assert.Ignore("Set DUBURL_SCHEMA_MYSQL to a disposable MySQL database.");
        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        Execute(connection, "DROP TABLE IF EXISTS child; DROP TABLE IF EXISTS parent; DROP TABLE IF EXISTS checks");
        try
        {
            var parent = Parent(new VarLengthColumn("id", DbType.String, 20, constraints: [new PrimaryKeyConstraint()],
                description: "Owner's préféré", nativeType: nativeType is null ? null : new NativeDatabaseType(nativeType)));
            var child = new Table("child", [new VarLengthColumn("id", DbType.String, 20, nativeType: nativeType is null ? null : new NativeDatabaseType(nativeType))],
                [new ForeignKey("fk_child_parent", ["id"], "parent", ["id"])], "Child's résumé");
            var renderer = new SchemaScriptRenderer(Dialect<MySqlDialect>());
            Execute(connection, renderer.Render(new SchemaModel([child, parent])));
            Execute(connection, "INSERT INTO parent VALUES ('STD'); INSERT INTO child VALUES ('STD')");
            Assert.That(() => Execute(connection, "INSERT INTO child VALUES ('invalid')"), Throws.Exception);
            Assert.That(Scalar(connection, "SELECT COLUMN_COMMENT FROM information_schema.columns WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME='parent' AND COLUMN_NAME='id'"), Is.EqualTo("Owner's préféré"));
            Assert.That(Scalar(connection, "SELECT TABLE_COMMENT FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME='child'"), Is.EqualTo("Child's résumé"));
            Execute(connection, renderer.Render(CheckedSchema(true)));
            Execute(connection, "INSERT INTO checks VALUES (0, 'STD', NULL, NULL, NULL, 'ABC')");
            Assert.That(() => Execute(connection, "INSERT INTO checks VALUES (-1, 'STD', NULL, NULL, NULL, 'ABC')"), Throws.Exception);
        }
        finally { Execute(connection, "DROP TABLE IF EXISTS child; DROP TABLE IF EXISTS parent; DROP TABLE IF EXISTS checks"); }
    }
}
