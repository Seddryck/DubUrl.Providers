using DubUrl.Discovery;
using DubUrl.Provider.Sqlite.Discovery;
using DubUrl.Provider.Sqlite.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace DubUrl.Provider.Sqlite.QA;

public class SqliteNamespaceTests
{
    private const string Attached = "aux.\"db";
    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await Execute(connection, $"ATTACH DATABASE ':memory:' AS {Q(Attached)}");
        return connection;
    }

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SqliteDialect>(["sqlite"]);
        return builder.Build().Get<SqliteDialect>();
    }

    [Test]
    public async Task MainAndAttachedDatabases_RoundTripIntoFreshScope()
    {
        await using var source = await Open();
        foreach (var database in new[] { "main", Attached })
        {
            var precision = database == "main" ? 10 : 12;
            var viewSource = database == "main" ? "parent" : Q(database) + ".parent";
            await Execute(source, $"""
                CREATE TABLE {Q(database)}.parent (b INTEGER NOT NULL,a INTEGER NOT NULL,title VARCHAR(40) DEFAULT 'hé',amount DECIMAL({precision},2),PRIMARY KEY(b,a),UNIQUE(title));
                CREATE TABLE {Q(database)}.child (x INTEGER,y INTEGER,FOREIGN KEY(x,y) REFERENCES parent(b,a));
                CREATE TABLE {Q(database)}.{Q("literal.table")} ("id.part" INTEGER NOT NULL,"quote""column" VARCHAR(20) DEFAULT 'literal',PRIMARY KEY("id.part"));
                CREATE INDEX {Q(database)}.{Q("index.name")} ON parent(title DESC,a ASC);
                CREATE VIEW {Q(database)}.{Q("view.name")} AS SELECT title AS "label.part" FROM {viewSource};
                """);
        }
        var options = new DiscoveryOptions
        {
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.PrimaryKeys, SchemaFeature.ForeignKeys,
                SchemaFeature.UniqueConstraints, SchemaFeature.Defaults, SchemaFeature.Indexes, SchemaFeature.Views]
        };
        var inspector = new SchemaInspector();
        var first = await inspector.InspectAsync(source, new SqliteMetadataReader(), options);
        Assert.That(first.IsComplete, Is.True, string.Join(";", first.Diagnostics));
        Assert.That(first.Schema!.Tables.Count, Is.EqualTo(6));
        Assert.That(first.Schema.Tables.Values.Select(table => table.Identity.Schema), Is.All.Null);
        Assert.That(first.Schema.Tables.Values.Select(table => table.Identity.Database).Distinct(), Is.EquivalentTo(new[] { "main", Attached }));
        Assert.That(((NumericColumn)first.Schema.Tables.Values.Single(table => table.Name == "parent" && table.Identity.Database == Attached).Columns["amount"]).Precision, Is.EqualTo(12));
        Assert.That(first.Schema.Views.Values.All(view => view.Identity.Database == view.Query.Source.Database), Is.True);
        var renderer = new SchemaScriptRenderer(Dialect());
        var script = renderer.Render(first.Schema);
        await using var target = await Open();
        await Execute(target, script);
        var second = await inspector.InspectAsync(target, new SqliteMetadataReader(), options);
        Assert.That(second.IsComplete, Is.True, string.Join(";", second.Diagnostics));
        // Rendering compares all supported mapped facets, defaults, ordered keys/indexes and view projections.
        Assert.That(renderer.Render(second.Schema!), Is.EqualTo(script));
        foreach (var database in new[] { "main", Attached })
            await Execute(target, $"INSERT INTO {Q(database)}.parent(b,a,title) VALUES (1,2,'ok'); INSERT INTO {Q(database)}.child(x,y) VALUES (1,2)");
        Assert.That(async () => await Execute(target, $"INSERT INTO {Q(Attached)}.child(x,y) VALUES (3,4)"), Throws.TypeOf<SqliteException>());

        var filtered = await inspector.InspectAsync(source, new SqliteMetadataReader(), options with { Databases = [Attached] });
        Assert.That(filtered.IsComplete, Is.True);
        Assert.That(filtered.Schema!.Tables.Count, Is.EqualTo(3));
        Assert.That(filtered.Schema.Views.Values.Single().Identity.Database, Is.EqualTo(Attached));
        Assert.That(filtered.ViewInventory.Single().Identity.Database, Is.EqualTo(Attached));
        var closure = await inspector.InspectAsync(source, new SqliteMetadataReader(), options with
        {
            Tables = [new("child", Database: Attached)], IncludeReferencedTables = true
        });
        Assert.That(closure.IsComplete, Is.True);
        Assert.That(closure.Schema!.Tables.Count, Is.EqualTo(2));
        Assert.That(closure.Schema.Tables.Values.All(table => table.Identity.Database == Attached), Is.True);
        Assert.That(closure.Schema.Tables.Values.Single(table => table.Name == "child").Constraints.OfType<ForeignKeyConstraint>().Single().TargetTable.Database, Is.EqualTo(Attached));
        var literal = await inspector.InspectAsync(source, new SqliteMetadataReader(), options with { Tables = [new("literal.table", Database: Attached)] });
        Assert.That(literal.IsComplete, Is.True);
        Assert.That(literal.Schema!.Tables.Values.Single().Identity, Is.EqualTo(new DatabaseObjectName("literal.table", database: Attached)));
        var absent = await inspector.InspectAsync(source, new SqliteMetadataReader(), options with { Schemas = ["main"] });
        Assert.That(absent.Schema!.Tables, Is.Empty);
        Assert.That(absent.ViewInventory, Is.Empty);
    }
}
