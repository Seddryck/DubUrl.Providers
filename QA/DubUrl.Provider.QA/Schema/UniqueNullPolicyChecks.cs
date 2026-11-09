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

public sealed record UniqueNullPolicyCase(UniqueNullPolicy Policy, string? Schema = null, string? Database = null,
    bool Named = true, bool UsesIndex = false, bool CanReference = true, bool DiscoverAsIndex = false,
    Func<string, string>? CatalogName = null, TablePhysicalOptions? PhysicalOptions = null);

public static class UniqueNullPolicyChecks
{
    public static async Task VerifyAsync(DbConnection connection, IDialect dialect, ISchemaMetadataReader reader,
        UniqueNullPolicyCase scenario, Func<Exception, bool> isDuplicate)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var identity = new DatabaseObjectName("null_policy_" + suffix, scenario.Schema, scenario.Database);
        var name = scenario.Named ? "uq_null_policy_" + suffix : null;
        var a = new Column("a", DbType.Int32, constraints: [new NullableConstraint()]);
        var b = new Column("b", DbType.Int32, constraints: [new NullableConstraint()]);
        var key = new UniquenessConstraint([b, a], name, scenario.Policy);
        var table = new Table(identity, [a, b], [key], null, scenario.PhysicalOptions);
        var child = new Table(new DatabaseObjectName("null_child_" + suffix, scenario.Schema, scenario.Database), [a, b],
            [new ForeignKeyConstraint("fk_null_policy_" + suffix, ["b", "a"], identity, ["b", "a"])]);
        var renderer = new SchemaScriptRenderer(dialect);
        var withForeignKey = new SchemaModel([table, child]);
        if (!scenario.CanReference)
            Assert.That(() => renderer.Plan(withForeignKey), Throws.TypeOf<NotSupportedException>().With.Message.Contains("no eligible planned UNIQUE target"));
        VerifyConflicts(renderer, table, key, a, b, suffix);

        var commands = renderer.Plan(scenario.CanReference ? withForeignKey : new SchemaModel([table]));
        Assert.That(commands.Count(command => command.Operation == SchemaSqlOperation.CreateIndex), Is.EqualTo(scenario.UsesIndex ? 1 : 0));
        if (name is not null)
            Assert.That(commands.Any(command => command.CommandText.Contains(dialect.SchemaSqlRenderer.RenderIdentifier(dialect, name), StringComparison.Ordinal)), Is.True);
        var tableSql = SqlName(dialect, identity);
        try
        {
            foreach (var command in commands)
                QaSql.Execute(connection, command.CommandText);

            var permitsNullDuplicates = scenario.Policy == UniqueNullPolicy.NullsDistinct;
            VerifyTuples(connection, tableSql, isDuplicate, permitsNullDuplicates, permitsNullDuplicates);
            if (scenario.CanReference)
                QaSql.Execute(connection, $"INSERT INTO {SqlName(dialect, child.Identity)} VALUES (10, 20)");

            await VerifyDiscoveryAsync(connection, reader, scenario, identity, name, renderer);
        }
        finally
        {
            QaSql.Execute(connection, $"DROP TABLE IF EXISTS {SqlName(dialect, child.Identity)}");
            QaSql.Execute(connection, $"DROP TABLE IF EXISTS {tableSql}");
        }
    }

    private static void VerifyConflicts(SchemaScriptRenderer renderer, Table table, UniquenessConstraint key,
        Column a, Column b, string suffix)
    {
        var identity = table.Identity;
        var duplicateIndex = new Index(new DatabaseObjectName("dup_" + suffix, identity.Schema, identity.Database), identity, [new("b"), new("a")], true);
        Assert.That(() => renderer.Plan(new SchemaModel([table], [duplicateIndex])),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("overlaps a separate unique index"));

        var sharded = new Table(identity, [a, b, new Column("outside_key", DbType.Int32)], [key],
            null, new(SchemaTableStorage.Rowstore, ["outside_key"]));
        var diagnostic = table.PhysicalOptions is null ? "does not support physical table/index options" : "shard key";
        Assert.That(() => renderer.Plan(new SchemaModel([sharded])),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains(diagnostic));
    }

    public static void RejectNullablePolicy(IDialect dialect, UniqueNullPolicy policy, TablePhysicalOptions? options = null)
    {
        var a = new Column("a", DbType.Int32, constraints: [new NullableConstraint()]);
        var b = new Column("b", DbType.Int32, constraints: [new NullableConstraint()]);
        var table = new Table(new DatabaseObjectName("unsupported_null_policy"), [a, b],
            [new UniquenessConstraint([b, a], null, policy)], null, options);
        Assert.That(() => new SchemaScriptRenderer(dialect).Plan(new([table])),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("cannot be enforced"));
    }

    public static void VerifyTuples(DbConnection connection, string tableSql, Func<Exception, bool> isDuplicate,
        bool allowsPartialNullDuplicates, bool allowsAllNullDuplicates)
    {
        void Insert(string values) => QaSql.Execute(connection, $"INSERT INTO {tableSql} VALUES ({values})");
        void Verify(string values, bool allowsDuplicate)
        {
            Insert(values);
            if (allowsDuplicate)
                Insert(values);
            else
            {
                var exception = Assert.Catch(() => Insert(values));
                Assert.That(isDuplicate(exception!), Is.True, $"Unexpected error for ({values}): {exception?.Message}");
            }
        }

        Verify("10, 20", false);
        Verify("NULL, 20", allowsPartialNullDuplicates);
        Verify("10, NULL", allowsPartialNullDuplicates);
        Verify("NULL, NULL", allowsAllNullDuplicates);
        Insert("NULL, 21");
        Insert("11, NULL");
    }

    private static async Task VerifyDiscoveryAsync(DbConnection connection, ISchemaMetadataReader reader,
        UniqueNullPolicyCase scenario, DatabaseObjectName identity, string? name, SchemaScriptRenderer renderer)
    {
        var inspected = await new SchemaInspector().InspectAsync(connection, reader, new()
        {
            Tables = [new(identity.Name, identity.Schema, identity.Database)],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.UniqueConstraints, SchemaFeature.Indexes]
        });
        Assert.That(inspected.IsComplete, Is.True, string.Join(", ", inspected.Diagnostics.Select(item => item.Message)));
        var discovered = inspected.Schema!;
        var table = discovered.Tables.Values.Single();
        var keys = table.Constraints.OfType<UniquenessConstraint>().ToArray();
        if (scenario.DiscoverAsIndex)
        {
            var index = discovered.Indexes.Values.Single();
            Assert.That(keys, Is.Empty);
            Assert.That(index.Name, Is.EqualTo(name));
            Assert.That(index.IsUnique, Is.True);
            Assert.That(index.Columns.Keys, Is.EqualTo(new[] { "b", "a" }));
        }
        else
        {
            var key = keys.Single();
            Assert.That(discovered.Indexes, Is.Empty);
            Assert.That(key.Name, Is.EqualTo(scenario.CatalogName?.Invoke(identity.Name) ?? name ?? $"{identity.Name}_b_a_key"));
            Assert.That(key.Columns.Select(column => column.Name), Is.EqualTo(new[] { "b", "a" }));
            Assert.That(key.NullPolicy, Is.EqualTo(scenario.Policy));
        }

        // SingleStore's shard/storage selection is supplied by the deployment fixture.
        if (scenario.PhysicalOptions is not null)
        {
            table = new Table(table.Identity, table.Columns.Values.ToArray(),
                table.Constraints.OfType<Constraint>().ToArray(), table.Description, scenario.PhysicalOptions);
            discovered = new SchemaModel([table], discovered.Indexes.Values.ToArray());
        }
        Assert.That(renderer.Plan(discovered).Count(command => command.Operation == SchemaSqlOperation.CreateIndex),
            Is.EqualTo(scenario.UsesIndex ? 1 : 0), "Replanning the catalog result must preserve a single enforcement path.");
    }

    public static string SqlName(IDialect dialect, DatabaseObjectName name)
        => dialect.SchemaSqlRenderer.RenderObjectName(dialect, name.Catalog, name.Database, name.Schema, name.Name);
}
