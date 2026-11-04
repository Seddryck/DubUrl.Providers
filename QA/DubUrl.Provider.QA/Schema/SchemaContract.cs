using DubUrl.Schema;
using SchemaModel = DubUrl.Schema.Schema;
using System.Data;
using System.Data.Common;
using DubUrl.Querying.Dialects;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Schema;

[Category("Schema")]
[FixtureLifeCycle(LifeCycle.SingleInstance)]
public abstract class SchemaContract
{
    protected abstract DbConnection CreateConnection();
    protected abstract IDialect CreateDialect();
    protected abstract SchemaCapabilities Capabilities { get; }
    protected virtual string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    protected virtual string PrepareCommandText(string script) => script;
    protected virtual string DropTableSql(string name)
        => $"drop table {(Capabilities.HasFlag(SchemaCapabilities.DropTableIfExists) ? "if exists " : "")}{QuoteIdentifier(name)}";

    [Test]
    [Category("SchemaBaseline")]
    public void DeclaresCreateTableCapability()
        => Assert.That(Capabilities.HasFlag(SchemaCapabilities.CreateTable), Is.True);

    [Test]
    [Category("SchemaBaseline")]
    public void RendersAndDeploysSchema()
    {
        var tableName = $"DubUrlQa_{Guid.NewGuid().ToString("N")[..20]}";
        var schema = new SchemaModel(
        [
            new Table(tableName,
            [
                new Column("Id", DbType.Int32),
                new VarLengthColumn("Name", DbType.AnsiString, 50),
            ]),
        ]);
        var script = new SchemaScriptRenderer(CreateDialect()).Render(schema);

        using var connection = CreateConnection();
        connection.Open();
        var created = false;
        try
        {
            using (var create = connection.CreateCommand())
            {
                create.CommandText = PrepareCommandText(script);
                create.ExecuteNonQuery();
                created = true;
            }

            using var query = connection.CreateCommand();
            query.CommandText = $"select count(*) from {QuoteIdentifier(tableName)}";
            Assert.That(Convert.ToInt64(query.ExecuteScalar()), Is.Zero);
        }
        finally
        {
            if (created)
            {
                using var drop = connection.CreateCommand();
                drop.CommandText = DropTableSql(tableName);
                drop.ExecuteNonQuery();
            }
        }
    }
}
