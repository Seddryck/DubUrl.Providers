using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.Oracle.Discovery;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Documents;
using NUnit.Framework;

namespace DubUrl.Provider.Oracle.QA.Schema.Specific;

[Category("Schema")]
public sealed class OracleColumnAnnotationRoundTripTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task DiscoveryDocumentAndDdlPreserveAnnotationsAndComments(bool nativeJson)
    {
        using var schema = OracleTestDatabase.CreateSchema();
        using var connection = schema.Connect();
        await connection.OpenAsync();
        var registry = new DialectRegistryBuilder();
        registry.AddDialect<OracleDialect>(["oracle"]);
        var version = Version.Parse(string.Join('.', connection.ServerVersion.Split('.').Take(2)));
        var renderer = new SchemaScriptRenderer(registry.Build().Get<OracleDialect>().WithServerVersion(version));
        const string description = "Owner's description";
        var annotations = new Dictionary<string, string> { ["myapp.owner"] = "équipe's data" };
        var column = nativeJson
            ? new Column("item code", new StructuredColumnType(new JsonType(), StructuredStorage.Json), description: description)
                { Annotations = annotations }
            : new Column("item code", DbType.Int32, description: description) { Annotations = annotations };
        var metadataReader = new OracleMetadataReader(version);

        async Task<Table> Discover(string name)
        {
            var result = await new SchemaInspector().InspectAsync(connection, metadataReader, new()
            {
                Tables = [new(name, schema.Name)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Comments, SchemaFeature.ColumnAnnotations]
            });
            Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
            return result.Schema!.Tables.Values.Single();
        }

        foreach (var command in renderer.Plan(new([new Table(new DatabaseObjectName("SOURCE_ITEMS", schema.Name), [column])])))
            QaSql.Execute(connection, command.CommandText);
        var source = await Discover("SOURCE_ITEMS");
        var discovered = source.Columns[column.Name];
        // Version 1 cannot encode Oracle's scalar native type scopes. JSON needs no projection.
        var exportTable = nativeJson ? source : new Table(source.Identity,
            [new Column(discovered.Name, column.Type, description: discovered.Description) { Annotations = discovered.Annotations }]);
        var document = new SchemaDocumentExporter().Map(new([exportTable]));
        document.Tables.Single().Identity.Name = "TARGET_ITEMS";
        foreach (var command in renderer.Plan(new SchemaDocumentMapper().Map(document)))
            QaSql.Execute(connection, command.CommandText);

        var recreated = (await Discover("TARGET_ITEMS")).Columns[column.Name];
        Assert.That(recreated.Description, Is.EqualTo(column.Description));
        Assert.That(recreated.Annotations, Is.EquivalentTo(column.Annotations));
    }
}
