using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Builders;
using DubUrl.Schema.Constraints;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using ForeignKeyConstraint = DubUrl.Schema.Constraints.ForeignKeyConstraint;

namespace DubUrl.Schema.SqlServer.QA;

[TestFixture]
[Category("SqlServer")]
[Category("Schema")]
public class SqlServerHierarchyRoundTripTests
{
    [Test]
    public async Task GeneratedHierarchyEnforcesParentPathsAndPreservesPhysicalAndLogicalMetadata()
    {
        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable SQL Server QA database."));
        await connection.OpenAsync();
        var dialects = new DialectRegistryBuilder();
        dialects.AddDialect<TSqlDialect>(["mssql"]);
        var renderer = new SchemaScriptRenderer(dialects.Build().Get<TSqlDialect>());
        var name = "hierarchy_" + Guid.NewGuid().ToString("N");
        var identity = new DatabaseObjectName(name, "dbo");
        var target = $"[dbo].[{name}]";
        var hierarchy = new Hierarchy("id", parentColumnName: "manager_id");
        var annotations = new Dictionary<string, string> { ["Owner"] = "finance" };
        var table = new TableBuilder().WithIdentity(identity)
            .WithColumns(columns => columns.Add(column => column.WithName("id").WithType(DbType.Int32)))
            .WithConstraints(constraints => constraints.AddPrimaryKey(key => key.WithColumnNames("id")))
            .WithAnnotations(annotations)
            .WithHierarchy(hierarchy).Build();

        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var plan = renderer.Plan(new([table]));
            foreach (var command in plan)
                await Execute(command.CommandText);
            await Execute($"INSERT INTO {target} (id, node_path) VALUES (1, hierarchyid::GetRoot());");
            await Execute($"INSERT INTO {target} (id, node_path) VALUES (2, hierarchyid::Parse('/1/'));");
            await Assert.ThatAsync(() => Execute($"INSERT INTO {target} (id, node_path) VALUES (3, hierarchyid::Parse('/7/1/'));"),
                Throws.TypeOf<SqlException>().With.Property(nameof(SqlException.Number)).EqualTo(547));
            await Assert.ThatAsync(() => Execute($"INSERT INTO {target} (id, node_path) VALUES (4, hierarchyid::Parse('/1/'));"),
                Throws.TypeOf<SqlException>().With.Property(nameof(SqlException.Number)).EqualTo(2627));

            var inspector = new SchemaInspector();
            var reader = new SqlServerMetadataReader();
            var options = new DiscoveryOptions { Tables = [new(name, "dbo")] };
            var result = await inspector.InspectAsync(connection, reader, options);
            Assert.That(result.IsComplete, Is.True, string.Join(Environment.NewLine, result.Diagnostics));
            var discovered = result.Schema!.Tables[identity.Key];

            Assert.Multiple(() =>
            {
                Assert.That(result.Hierarchies[identity], Is.EqualTo(hierarchy));
                Assert.That(discovered.Annotations, Is.EquivalentTo(annotations));
                Assert.That(discovered.Constraints.Get<PrimaryKeyConstraint>()?.Columns.Keys, Is.EqualTo(new[] { "id" }));
                Assert.That(discovered.Columns["node_path"].NativeType?.Name, Is.EqualTo("hierarchyid"));
                Assert.That(discovered.Columns["parent_path"].Generation, Is.TypeOf<GeneratedColumnGeneration>());
                Assert.That(discovered.Constraints.Get<UniquenessConstraint>()?.Columns.Single().Name, Is.EqualTo("node_path"));
                Assert.That(discovered.Constraints.Get<ForeignKeyConstraint>()?.TargetTable, Is.EqualTo(identity));
            });

            // Render the physical schema recovered from the catalog, including its computed parent.
            await Execute($"DROP TABLE {target};");
            foreach (var command in renderer.Plan(result.Schema))
                await Execute(command.CommandText);
            await Execute($"INSERT INTO {target} (id, node_path) VALUES (1, hierarchyid::GetRoot());");
            await Assert.ThatAsync(() => Execute($"INSERT INTO {target} (id, node_path) VALUES (2, hierarchyid::Parse('/7/1/'));"),
                Throws.TypeOf<SqlException>().With.Property(nameof(SqlException.Number)).EqualTo(547));

            // A physical hierarchy alone does not identify a DubUrl declaration.
            var physicalOnly = await inspector.InspectAsync(connection, reader, options);
            Assert.That(physicalOnly.IsComplete, Is.True, string.Join(Environment.NewLine, physicalOnly.Diagnostics));
            Assert.That(physicalOnly.Hierarchies, Is.Empty);
            Assert.That(physicalOnly.Schema!.Tables[identity.Key].Annotations, Is.EquivalentTo(annotations));
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS {target};");
        }
    }
}
