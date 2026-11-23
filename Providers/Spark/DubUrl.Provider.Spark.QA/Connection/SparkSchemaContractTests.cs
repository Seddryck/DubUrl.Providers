using DubUrl.Provider.Spark.QA;
using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Schema;
using DubUrl.Schema.Documents;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Connection;

public sealed class SparkSchemaContractTests : SparkSchemaProviderContract
{
    protected override bool IsDelta => false;
    protected override ConnectionUrl UnscopedUrl => new(SparkOdbcTests.ThriftUrl + "&UseNativeQuery=1");
    protected override ConnectionUrl ScopedUrl(string catalog, string schema)
        => new($"odbc+spark+tcp://127.0.0.1/schema/{Uri.EscapeDataString(schema)}?AuthMech=0&sasl=0&UseNativeQuery=1");

    [Test]
    public Task ColumnAnnotationsAndCommentsRoundTripThroughDocuments()
        => SparkSchemaQa.WithScratchAsync(UnscopedUrl, async (connection, catalog, schema) =>
        {
            var renderer = new SchemaScriptRenderer(UnscopedUrl.Dialect);
            var column = new Column("id", DbType.Int32, description: "Owner's description")
            { Annotations = new Dictionary<string, string> { ["myapp.owner"] = @"équipe's \data" } };
            var reader = new SparkMetadataReader();

            async Task<Table> Discover(string name)
            {
                var result = await new SchemaInspector().InspectAsync(connection, reader, new()
                {
                    Tables = [new(name, schema, Catalog: catalog)],
                    Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Comments, SchemaFeature.ColumnAnnotations]
                });
                Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
                return result.Schema!.Tables.Values.Single();
            }

            var source = new Table(new DatabaseObjectName("source_items", schema, null, catalog), [column]);
            foreach (var command in renderer.Plan(new([source])))
                SparkSchemaQa.Execute(connection, command.CommandText);
            var document = new SchemaDocumentExporter().Map(new([await Discover("source_items")]));
            document.Tables.Single().Identity.Name = "target_items";
            foreach (var command in renderer.Plan(new SchemaDocumentMapper().Map(document)))
                SparkSchemaQa.Execute(connection, command.CommandText);

            var recreated = (await Discover("target_items")).Columns[column.Name];
            Assert.That(recreated.Description, Is.EqualTo(column.Description));
            Assert.That(recreated.Annotations, Is.EquivalentTo(column.Annotations));
        });
}
