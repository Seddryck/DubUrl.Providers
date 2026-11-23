using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Documents;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace DubUrl.Provider.MsSqlServer.QA;

[Category("Schema")]
public sealed class SqlServerColumnAnnotationRoundTripTests
{
    [Test]
    public async Task DiscoveryDocumentAndDdlPreserveTagsPropertiesAndComments()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
            Assert.Ignore("Configure a disposable SQL Server discovery QA database.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var registry = new DialectRegistryBuilder();
        registry.AddDialect<TSqlDialect>(["mssql"]);
        var dialect = registry.Build().Get<TSqlDialect>();
        var source = "source_" + Guid.NewGuid().ToString("N");
        var target = "target_" + Guid.NewGuid().ToString("N");
        var column = new Column("item code", DbType.Int32, description: "Owner's description")
        {
            Annotations = new Dictionary<string, string> { ["pii"] = "column annotation", ["myapp.owner"] = "équipe's data" },
            Tags = new Dictionary<string, string> { ["pii"] = "true", ["PII"] = "false", ["équipe"] = "" }
        };
        var table = new Table(new DatabaseObjectName(source, "dbo"), [column], null, "Table description", null,
            new Dictionary<string, string> { ["pii"] = "table annotation" })
        {
            Tags = new Dictionary<string, string> { ["pii"] = "false" }
        };
        var renderer = new SchemaScriptRenderer(dialect);
        var inspector = new SchemaInspector();
        var metadataReader = new SqlServerMetadataReader();

        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        async Task<Table> Discover(string name)
        {
            var result = await inspector.InspectAsync(connection, metadataReader, new()
            {
                Tables = [new(name, "dbo")],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Comments, SchemaFeature.Annotations, SchemaFeature.ColumnAnnotations]
            });
            Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
            Assert.That(result.TableTagCoverage, Is.EqualTo(MetadataCoverage.Available));
            Assert.That(result.ColumnTagCoverage, Is.EqualTo(MetadataCoverage.Available));
            return result.Schema!.Tables.Values.Single();
        }

        try
        {
            foreach (var command in renderer.Plan(new([table])))
                await Execute(command.CommandText);
            var first = await Discover(source);
            var document = new SchemaDocumentExporter().Map(new([first]));
            document.Tables.Single().Identity.Name = target;
            foreach (var command in renderer.Plan(new SchemaDocumentMapper().Map(document)))
                await Execute(command.CommandText);
            var second = await Discover(target);
            Assert.Multiple(() =>
            {
                Assert.That(first.Tags, Is.EquivalentTo(table.Tags));
                Assert.That(first.Columns[column.Name].Tags, Is.EquivalentTo(column.Tags));
                Assert.That(second.Description, Is.EqualTo(table.Description));
                Assert.That(second.Annotations, Is.EquivalentTo(table.Annotations));
                Assert.That(second.Tags, Is.EquivalentTo(table.Tags));
                Assert.That(second.Columns[column.Name].Description, Is.EqualTo(column.Description));
                Assert.That(second.Columns[column.Name].Annotations, Is.EquivalentTo(column.Annotations));
                Assert.That(second.Columns[column.Name].Tags, Is.EquivalentTo(column.Tags));
            });
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS dbo.[{source}]; DROP TABLE IF EXISTS dbo.[{target}];");
        }
    }
}
