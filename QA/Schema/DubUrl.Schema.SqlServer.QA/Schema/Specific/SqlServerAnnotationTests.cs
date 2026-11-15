using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Documents;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace DubUrl.Schema.SqlServer.QA;

public sealed class SqlServerAnnotationTests
{
    [Test]
    [Category("Schema")]
    public async Task DiscoversAndRecreatesTableTextProperties()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
            Assert.Ignore("Configure a disposable SQL Server discovery QA database.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var dialect = Dialect();
        var name = "annotations_'雪." + Guid.NewGuid().ToString("N");
        var qualified = dialect.SchemaSqlRenderer.RenderObjectName(dialect, null, null, "dbo", name);
        var options = new DiscoveryOptions
        {
            Tables = [new(name, "dbo")],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Comments, SchemaFeature.Annotations],
            AllowPartial = true,
        };
        ISchemaMetadataReader reader = new SqlServerMetadataReader();
        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        async Task AddProperty(string key, object value, string? column = null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                EXEC sys.sp_addextendedproperty @name = @key, @value = @value,
                  @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = @table
                """ + (column is null ? ";" : ", @level2type = N'COLUMN', @level2name = @column;");
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            command.Parameters.AddWithValue("@table", name);
            if (column is not null)
                command.Parameters.AddWithValue("@column", column);
            await command.ExecuteNonQueryAsync();
        }

        var annotations = new Dictionary<string, string>
        {
            ["MyApp.Owner's"] = "L'équipe 雪\\data",
            ["MyApp.Empty"] = "",
            ["MyApp.Boundary"] = new string('雪', 3750),
        };
        try
        {
            await Execute($"CREATE TABLE {qualified} ([Id] INT NOT NULL);");
            await AddProperty("MS_Description", "Owner's table");
            foreach (var (key, value) in annotations)
                await AddProperty(key, value);
            await AddProperty("ColumnOnly", "excluded", "Id");
            await AddProperty("Numeric", 42);
            await AddProperty("NullValue", DBNull.Value);

            var withoutAnnotations = await reader.ReadAsync(connection, options with
                { Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Comments] });
            Assert.That(withoutAnnotations.Tables.Single(table => table.Identity.Name == name).Annotations, Is.Empty);

            var inspector = new SchemaInspector();
            var original = await inspector.InspectAsync(connection, reader, options);
            Assert.That(original.Coverage[SchemaFeature.Annotations], Is.EqualTo(MetadataCoverage.Unrepresentable));
            Assert.That(original.Diagnostics.Where(item => item.Feature == SchemaFeature.Annotations)
                .Select(item => item.Message), Has.Some.Contains("Numeric").And.Some.Contains("NullValue"));
            Assert.That(original.Schema!.Tables.Values.Single().Annotations, Is.EquivalentTo(annotations));
            using var document = new MemoryStream();
            new SchemaDocumentWriter().Write(document, new SchemaDocumentExporter().Map(original.Schema), SchemaDocumentFormat.Json);
            document.Position = 0;
            var schema = new SchemaDocumentMapper().Map(new SchemaDocumentReader().Read(document, SchemaDocumentFormat.Json));
            var script = new SchemaScriptRenderer(dialect).Render(schema);

            await Execute($"DROP TABLE {qualified};");
            await Execute(script);
            var recreated = await inspector.InspectAsync(connection, reader, options);
            Assert.That(recreated.IsComplete, Is.True, string.Join(", ", recreated.Diagnostics.Select(item => item.Message)));
            Assert.That(recreated.Schema!.Tables.Values.Single().Description, Is.EqualTo("Owner's table"));
            Assert.That(recreated.Schema.Tables.Values.Single().Annotations, Is.EquivalentTo(annotations));
        }
        finally
        {
            await Execute($"DROP TABLE IF EXISTS {qualified};");
        }
    }

    [Test]
    public void PlanRejectsPropertiesThatCannotBeWrittenAndOmitsEmptyMaps()
    {
        var dialect = Dialect();
        var renderer = (Renderers.ITableAnnotationSqlRenderer)dialect.SchemaSqlRenderer;
        var table = new DatabaseObjectName("Example");
        Assert.That(renderer.PlanTableAnnotations(dialect, table, new Dictionary<string, string>()), Is.Empty);
        Assert.That(() => renderer.PlanTableAnnotations(dialect, table,
            new Dictionary<string, string> { ["ms_description"] = "text" }), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => renderer.PlanTableAnnotations(dialect, table,
            new Dictionary<string, string> { [new string('x', 129)] = "text" }), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => renderer.PlanTableAnnotations(dialect, table,
            new Dictionary<string, string> { ["Large"] = new string('x', 3751) }), Throws.TypeOf<NotSupportedException>());
    }

    private static TSqlDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssql"]);
        return builder.Build().Get<TSqlDialect>();
    }
}
