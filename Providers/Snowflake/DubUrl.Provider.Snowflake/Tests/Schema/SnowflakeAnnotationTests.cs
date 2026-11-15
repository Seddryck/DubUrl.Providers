using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.Snowflake.Discovery;
using DubUrl.Schema;
using DubUrl.Schema.Documents;
using DubUrl.Schema.Renderers;
using NUnit.Framework;
using Snowflake.Data.Client;

namespace DubUrl.Provider.Snowflake.Tests.Schema;

public class SnowflakeAnnotationTests
{
    [TestCase("TAG")]
    [TestCase("GOV.TAGS.X; DROP TABLE ITEMS")]
    [TestCase("SNOWFLAKE.CORE.SEMANTIC_CATEGORY")]
    public void PlanRejectsInvalidTagKeys(string key)
    {
        var dialect = SnowflakeStructuredTypeTests.Dialect();
        var renderer = (ITableAnnotationSqlRenderer)dialect.SchemaSqlRenderer;
        var table = new DatabaseObjectName("ITEMS");
        Assert.That(renderer.PlanTableAnnotations(dialect, table, new Dictionary<string, string>()), Is.Empty);
        Assert.That(() => renderer.PlanTableAnnotations(dialect, table, new Dictionary<string, string> { [key] = "text" }),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    [Explicit("Requires a disposable schema with tag privileges in DUBURL_SNOWFLAKE_QA_CONNECTION_STRING.")]
    public async Task RoundTripsDirectTagsAndExcludesColumnTags()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_SNOWFLAKE_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable Snowflake schema.");
        await using var connection = new SnowflakeDbConnection { ConnectionString = connectionString };
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N").ToUpperInvariant();
        var name = "DUBURL_TAG_TABLE_" + suffix;
        var directTag = "DUBURL_DIRECT_" + suffix;
        var columnTag = "DUBURL_COLUMN_" + suffix;
        var renderer = new SchemaScriptRenderer(SnowflakeStructuredTypeTests.Dialect());
        var options = new DiscoveryOptions
        {
            Tables = [new(name)],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Annotations],
        };
        var inspector = new SchemaInspector();
        var reader = new SnowflakeMetadataReader();
        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await Execute($"CREATE TAG {directTag};");
            await Execute($"CREATE TAG {columnTag};");
            await Execute($"CREATE TABLE {name} (ID NUMBER(10,0));");
            await Execute($"""ALTER TABLE {name} SET TAG {directTag} = 'L''équipe\\data';""");
            await Execute($"ALTER TABLE {name} MODIFY COLUMN ID SET TAG {columnTag} = 'column only';");
            var original = await inspector.InspectAsync(connection, reader, options);
            Assert.That(original.IsComplete, Is.True, string.Join(", ", original.Diagnostics.Select(item => item.Message)));
            var annotation = original.Schema!.Tables.Values.Single().Annotations.Single();
            Assert.That(annotation.Key, Does.EndWith("." + directTag));
            Assert.That(annotation.Value, Is.EqualTo("L'équipe\\data"));

            using var stream = new MemoryStream();
            new SchemaDocumentWriter().Write(stream, new SchemaDocumentExporter().Map(original.Schema), SchemaDocumentFormat.Json);
            stream.Position = 0;
            var schema = new SchemaDocumentMapper().Map(new SchemaDocumentReader().Read(stream, SchemaDocumentFormat.Json));
            await Execute($"DROP TABLE {name};");
            foreach (var command in renderer.Plan(schema))
                await Execute(command.CommandText);

            var recreated = await inspector.InspectAsync(connection, reader, options);
            Assert.That(recreated.IsComplete, Is.True, string.Join(", ", recreated.Diagnostics.Select(item => item.Message)));
            Assert.That(recreated.Schema!.Tables.Values.Single().Annotations, Is.EqualTo(new[] { annotation }));
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS {name};");
            await Execute($"DROP TAG IF EXISTS {columnTag};");
            await Execute($"DROP TAG IF EXISTS {directTag};");
        }
    }
}
