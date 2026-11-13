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
    public async Task DiscoveryDocumentAndDdlPreservePropertiesAndComments()
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
        { Annotations = new Dictionary<string, string> { ["myapp.owner"] = "équipe's data" } };
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
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Comments, SchemaFeature.ColumnAnnotations]
            });
            Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
            return result.Schema!.Tables.Values.Single();
        }

        try
        {
            foreach (var command in renderer.Plan(new([new Table(new DatabaseObjectName(source, "dbo"), [column])])))
                await Execute(command.CommandText);
            var first = await Discover(source);
            var document = new SchemaDocumentExporter().Map(new([first]));
            document.Tables.Single().Identity.Name = target;
            foreach (var command in renderer.Plan(new SchemaDocumentMapper().Map(document)))
                await Execute(command.CommandText);
            var second = (await Discover(target)).Columns[column.Name];
            Assert.That(second.Description, Is.EqualTo(column.Description));
            Assert.That(second.Annotations, Is.EquivalentTo(column.Annotations));
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS dbo.[{source}]; DROP TABLE IF EXISTS dbo.[{target}];");
        }
    }
}
