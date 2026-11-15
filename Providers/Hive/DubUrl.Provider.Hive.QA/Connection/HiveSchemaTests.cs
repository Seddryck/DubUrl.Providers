using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Discovery;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Documents;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

[Category("Schema")]
[Category("ODBC")]
public sealed class HiveSchemaTests
{
    [Test]
    public void RegistersColumnAnnotationDiscovery()
    {
        var url = new ConnectionUrl(HiveQaDatabase.GetUrl(useNativeQuery: true));

        Assert.That(url.TryGetCapability<SchemaDiscoveryCapability>(out var capability), Is.True);
        Assert.That(capability!.Reader.Capabilities[SchemaFeature.ColumnAnnotations], Is.EqualTo(MetadataCoverage.Available));
    }

    [Test]
    public async Task GeneratedScalarTableCanBeCreatedAndQueried()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        var url = new ConnectionUrl(HiveQaDatabase.GetUrl(useNativeQuery: true));
        var name = "duburl_schema_" + Guid.NewGuid().ToString("N");
        var copyName = name + "_copy";
        var column = new Column("id", DbType.Int32, description: "Identifier")
        { Annotations = new Dictionary<string, string> { ["myapp.owner"] = "team's data" } };
        var table = new Table(new DatabaseObjectName(name), [column], null, null, null,
            new Dictionary<string, string> { ["myapp.source"] = "hive-qa" });
        var plan = new SchemaScriptRenderer(url.Dialect).Plan(new DubUrl.Schema.Schema([table]));
        var target = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, null, null, null, name);
        var copy = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, null, null, null, copyName);

        using var connection = (DbConnection)url.Connect();
        connection.Open();
        try
        {
            foreach (var command in plan)
                QaSql.Execute(connection, command.CommandText);

            using (var properties = connection.CreateCommand())
            {
                properties.CommandText = $"SHOW TBLPROPERTIES {target} ('myapp.source')";
                using var property = properties.ExecuteReader();
                Assert.That(property.Read(), Is.True);
                Assert.That(property.GetString(0), Is.EqualTo("myapp.source"));
                Assert.That(property.GetString(1), Is.EqualTo("hive-qa"));
            }

            Task<SchemaInspectionResult> Discover(string tableName) => new SchemaInspector().InspectAsync(url, new()
            {
                Tables = [new(tableName)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Comments,
                    SchemaFeature.Annotations, SchemaFeature.ColumnAnnotations]
            });
            var schema = await Discover(name);
            Assert.That(schema.IsComplete, Is.True, string.Join(", ", schema.Diagnostics.Select(item => item.Message)));
            var document = new SchemaDocumentExporter().Map(schema.Schema!);
            document.Tables.Single().Identity.Name = copyName;
            foreach (var command in new SchemaScriptRenderer(url.Dialect).Plan(new SchemaDocumentMapper().Map(document)))
                QaSql.Execute(connection, command.CommandText);
            var recreated = await Discover(copyName);
            Assert.That(recreated.IsComplete, Is.True, string.Join(", ", recreated.Diagnostics.Select(item => item.Message)));
            var discovered = recreated.Schema!.Tables.Values.Single().Columns["id"];
            Assert.That(discovered.Description, Is.EqualTo(column.Description));
            Assert.That(discovered.Annotations, Is.EquivalentTo(column.Annotations));

            using var query = connection.CreateCommand();
            query.CommandText = $"SELECT id FROM {target} WHERE 1 = 0";
            using var rows = query.ExecuteReader();
            Assert.That(rows.GetName(0).Split('.').Last(), Is.EqualTo("id").IgnoreCase);
            Assert.That(rows.GetFieldType(0), Is.EqualTo(typeof(int)));
            Assert.That(rows.Read(), Is.False);
        }
        finally
        {
            QaSql.Execute(connection, $"DROP TABLE IF EXISTS {target}");
            QaSql.Execute(connection, $"DROP TABLE IF EXISTS {copy}");
        }
    }
}
