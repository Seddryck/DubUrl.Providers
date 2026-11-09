using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using DbType = System.Data.DbType;
using Index = DubUrl.Schema.Index;
using SchemaModel = DubUrl.Schema.Schema;

namespace DubUrl.Providers.QA.Contracts.Schema;

public static class UniqueNullPolicyChecks
{
    public static async Task VerifyAsync(DbConnection connection, IDialect dialect, ISchemaMetadataReader reader,
        string scope, UniqueNullPolicy policy, Func<Exception, bool> isDuplicate, bool usesIndex = false,
        bool named = true, bool discoverAsIndex = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var identity = new DatabaseObjectName("null_policy_" + suffix, scope);
        var name = named ? "uq_null_policy_" + suffix : null;
        var a = new Column("a", DbType.Int32, constraints: [new NullableConstraint()]);
        var b = new Column("b", DbType.Int32, constraints: [new NullableConstraint()]);
        var table = new Table(identity, [a, b], [new UniquenessConstraint([b, a], name, policy)]);
        var child = new Table(new DatabaseObjectName("null_child_" + suffix, scope), [a, b],
            [new ForeignKeyConstraint("fk_null_policy_" + suffix, ["b", "a"], identity, ["b", "a"])]);
        var renderer = new SchemaScriptRenderer(dialect);
        var withForeignKey = new SchemaModel([table, child]);
        if (usesIndex)
            Assert.That(() => renderer.Plan(withForeignKey), Throws.TypeOf<NotSupportedException>().With.Message.Contains("no eligible planned UNIQUE target"));

        var duplicateIndex = new Index(new DatabaseObjectName("dup_" + suffix, scope), identity, [new("b"), new("a")], true);
        Assert.That(() => renderer.Plan(new SchemaModel([table], [duplicateIndex])),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("overlaps a separate unique index"));

        var sharded = new Table(identity, [a, b], [new UniquenessConstraint([b, a], name, policy)],
            null, new(SchemaTableStorage.Rowstore, ["b"]));
        Assert.That(() => renderer.Plan(new SchemaModel([sharded])),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("does not support physical table/index options"));

        var commands = renderer.Plan(usesIndex ? new SchemaModel([table]) : withForeignKey);
        Assert.That(commands.Count(command => command.Operation == SchemaSqlOperation.CreateIndex), Is.EqualTo(usesIndex ? 1 : 0));
        var tableSql = SqlName(dialect, identity);
        void Insert(string values) => QaSql.Execute(connection, $"INSERT INTO {tableSql} VALUES ({values})");
        void Duplicate(string values)
        {
            var exception = Assert.Catch(() => Insert(values));
            Assert.That(isDuplicate(exception!), Is.True, $"Unexpected error for ({values}): {exception?.Message}");
        }

        try
        {
            foreach (var command in commands)
                QaSql.Execute(connection, command.CommandText);

            Insert("10, 20");
            Duplicate("10, 20");
            foreach (var tuple in new[] { "NULL, 20", "10, NULL", "NULL, NULL" })
            {
                Insert(tuple);
                if (policy == UniqueNullPolicy.NullsDistinct)
                    Insert(tuple);
                else
                    Duplicate(tuple);
            }
            Insert("NULL, 21");
            Insert("11, NULL");
            if (!usesIndex)
                QaSql.Execute(connection, $"INSERT INTO {SqlName(dialect, child.Identity)} VALUES (10, 20)");

            var inspected = await new SchemaInspector().InspectAsync(connection, reader, new()
            {
                Tables = [new(identity.Name, identity.Schema)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.UniqueConstraints, SchemaFeature.Indexes]
            });
            Assert.That(inspected.IsComplete, Is.True, string.Join(", ", inspected.Diagnostics.Select(item => item.Message)));
            var keys = inspected.Schema!.Tables.Values.Single().Constraints.OfType<UniquenessConstraint>().ToArray();
            if (discoverAsIndex)
            {
                var index = inspected.Schema.Indexes.Values.Single();
                Assert.That(keys, Is.Empty);
                Assert.That(index.Name, Is.EqualTo(name));
                Assert.That(index.IsUnique, Is.True);
                Assert.That(index.Columns.Keys, Is.EqualTo(new[] { "b", "a" }));
            }
            else
            {
                var key = keys.Single();
                Assert.That(inspected.Schema.Indexes, Is.Empty);
                Assert.That(key.Name, Is.EqualTo(name ?? $"{identity.Name}_b_a_key"));
                Assert.That(key.Columns.Select(column => column.Name), Is.EqualTo(new[] { "b", "a" }));
                Assert.That(key.NullPolicy, Is.EqualTo(policy));
            }
            Assert.That(renderer.Plan(inspected.Schema).Count(command => command.Operation == SchemaSqlOperation.CreateIndex),
                Is.EqualTo(usesIndex ? 1 : 0), "Replanning the catalog result must preserve a single enforcement path.");
        }
        finally
        {
            QaSql.Execute(connection, $"DROP TABLE IF EXISTS {SqlName(dialect, child.Identity)}");
            QaSql.Execute(connection, $"DROP TABLE IF EXISTS {tableSql}");
        }
    }

    private static string SqlName(IDialect dialect, DatabaseObjectName name)
        => dialect.SchemaSqlRenderer.RenderObjectName(dialect, name.Catalog, name.Database, name.Schema, name.Name);
}
