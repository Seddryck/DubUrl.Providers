using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Discovery;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

[Category("Schema")]
[Category("ODBC")]
public sealed class HiveSchemaTests
{
    [Test]
    public async Task InspectionReportsUnavailableDiscovery()
    {
        var url = new ConnectionUrl(HiveQaDatabase.GetUrl(useNativeQuery: true));

        Assert.That(url.TryGetCapability<SchemaDiscoveryCapability>(out _), Is.False);
        var result = await new SchemaInspector().InspectAsync(url);
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Diagnostics.Select(item => item.Code), Is.EqualTo(new[] { "SDI001" }));
    }

    [Test]
    public void GeneratedScalarTableCanBeCreatedAndQueried()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        var url = new ConnectionUrl(HiveQaDatabase.GetUrl(useNativeQuery: true));
        var name = "duburl_schema_" + Guid.NewGuid().ToString("N");
        var table = new Table(new DatabaseObjectName(name), [new Column("id", DbType.Int32)], null, null, null,
            new Dictionary<string, string> { ["myapp.source"] = "hive-qa" });
        var plan = new SchemaScriptRenderer(url.Dialect).Plan(new DubUrl.Schema.Schema([table]));
        var target = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, null, null, null, name);

        using var connection = (DbConnection)url.Connect();
        connection.Open();
        try
        {
            foreach (var command in plan)
                QaSql.Execute(connection, command.CommandText);

            using var properties = connection.CreateCommand();
            properties.CommandText = $"SHOW TBLPROPERTIES {target} ('myapp.source')";
            Assert.That(properties.ExecuteScalar(), Is.EqualTo("hive-qa"));

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
        }
    }
}
