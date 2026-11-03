using System.Data;
using System.Data.Common;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Sqlite.QA;

public sealed partial class SqliteSchemaTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Identity_PublicDeploymentGeneratesKeysAndDoesNotReuseDeletedValues(bool tableKey)
    {
        using var target = CreatePublicDatabase();
        var id = new Column("id", DbType.Int64, constraints: tableKey ? [] : [new PrimaryKeyConstraint("pk_identity")],
            generation: new IdentityColumnGeneration());
        var table = new Table("identity_values", [id, new Column("payload", DbType.Int32)],
            tableKey ? [new PrimaryKeyConstraint(id, "pk_identity")] : []);
        target.Url.DeploySchema(new DubUrl.Schema.Schema([table]));
        using var connection = (DbConnection)target.Url.Open();
        QaSql.Execute(connection, "INSERT INTO identity_values (payload) VALUES (10),(20)");
        Assert.That(QaSql.Rows(connection, "SELECT id FROM identity_values ORDER BY id").Select(row => Convert.ToInt64(row[0])),
            Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(() => QaSql.Execute(connection, "INSERT INTO identity_values (id,payload) VALUES (1,30)"),
            Throws.InstanceOf<DbException>());
        QaSql.Execute(connection, "DELETE FROM identity_values WHERE id=2; INSERT INTO identity_values (payload) VALUES (30)");
        Assert.That(QaSql.Rows(connection, "SELECT id FROM identity_values ORDER BY id").Select(row => Convert.ToInt64(row[0])),
            Is.EqualTo(new long[] { 1, 3 }));
        Assert.That(Convert.ToInt64(QaSql.Rows(connection, "PRAGMA table_info(identity_values)").Single(row => (string)row[1]! == "id")[5]), Is.EqualTo(1));
    }

    [TestCase("composite")]
    [TestCase("noninteger")]
    [TestCase("nokey")]
    [TestCase("nullable")]
    [TestCase("seed")]
    [TestCase("increment")]
    public void Identity_PublicDeploymentRejectsUnrepresentableDeclarations(string kind)
    {
        using var target = CreatePublicDatabase();
        var id = new Column("id", kind == "noninteger" ? DbType.String : DbType.Int32,
            constraints: kind == "nullable" ? [new NullableConstraint()] : [],
            generation: new IdentityColumnGeneration(kind == "seed" ? 10 : 1, kind == "increment" ? 2 : 1));
        var payload = new Column("payload", DbType.Int32);
        var table = new Table("invalid_identity", [id, payload], kind == "nokey" ? []
            : [new PrimaryKeyConstraint(kind == "composite" ? [id, payload] : [id])]);
        Assert.That(() => target.Url.DeploySchema(new DubUrl.Schema.Schema([table])), Throws.TypeOf<NotSupportedException>());
        using var connection = (DbConnection)target.Url.Open();
        Assert.That(Convert.ToInt64(QaSql.Rows(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='invalid_identity'").Single()[0]), Is.Zero);
    }
}
