using System.Data;
using System.Data.Common;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.DuckDb.QA;

public sealed partial class DuckDbBaselineTests
{
    [Test]
    [Category("PublicCommandPlan")]
    public void PublicCommandPlan_SequencePrerequisitesDeployAndResetGeneratedValues()
    {
        using var target = CreatePublicDatabase();
        var dialect = target.Url.Dialect;
        var provider = dialect.SchemaSqlRenderer;
        var sequence = (ISequenceIdentitySqlRenderer)provider;
        var prefix = "Cs" + Guid.NewGuid().ToString("N")[..8];
        var tables = new[] {
            new Table(prefix + "a", [new Column("id", DbType.Int64, generation: new IdentityColumnGeneration(10, 2))]),
            new Table(prefix + "b", [new Column("id", DbType.Int64, generation: new IdentityColumnGeneration(20, 3))]) };
        var schema = new Schema(tables);
        try
        {
            target.Url.DeploySchema(schema);
            for (var pass = 0; pass < 2; pass++)
            {
                using var connection = (DbConnection)target.Url.Open();
                for (var i = 0; i < tables.Length; i++)
                {
                    var name = provider.RenderObjectName(dialect, null, null, null, tables[i].Name);
                    QaSql.Execute(connection, $"INSERT INTO {name} DEFAULT VALUES");
                    QaSql.Execute(connection, $"INSERT INTO {name} DEFAULT VALUES");
                    var actual = QaSql.Rows(connection, $"SELECT * FROM {name} ORDER BY id").Select(row => Convert.ToInt64(row[0]));
                    Assert.That(actual, Is.EqualTo(i == 0 ? new long[] { 10, 12 } : new long[] { 20, 23 }));
                }
                if (pass == 0) target.Url.DeploySchema(schema, SchemaCreationOptions.DropIfExists);
            }
        }
        finally
        {
            using var connection = (DbConnection)target.Url.Open();
            foreach (var table in tables.Reverse())
            {
                var name = provider.RenderObjectName(dialect, null, null, null, table.Name);
                new SchemaScriptDeployer().DeploySchema(target.Url, provider.PlanCommands(dialect,
                    [new($"DROP TABLE IF EXISTS {name};", SchemaSqlOperation.DropTable, table.Name)]));
                var context = new IdentityColumnContext(table.Name, "id");
                // Explicit provider prerequisite units allow cleanup without parsing the legacy script.
                var dropSequence = sequence.PlanIdentityPrerequisites(dialect, context, 1, 1, true).First();
                new SchemaScriptDeployer().DeploySchema(target.Url, new[] { dropSequence });
                Assert.That(ObjectExists(connection, table.Identity, false), Is.False);
            }
            Assert.That(Convert.ToInt64(QaSql.Rows(connection, "SELECT COUNT(*) FROM duckdb_sequences()").Single()[0]), Is.Zero);
        }
    }
}
