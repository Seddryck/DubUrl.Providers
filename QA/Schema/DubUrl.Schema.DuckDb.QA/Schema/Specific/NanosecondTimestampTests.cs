using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Documents;
using DuckDB.NET.Data;
using NUnit.Framework;

namespace DubUrl.Schema.DuckDb.QA;

[TestFixture]
[Category("DuckDb")]
[Category("Schema")]
public sealed class NanosecondTimestampTests
{
    [Test]
    public async Task InspectAndRender_PreservesNanosecondTimestampValues()
    {
        using var source = new DuckDBConnection("DataSource=:memory:");
        source.Open();
        QaSql.Execute(source, "CREATE TABLE times (stamp TIMESTAMP_NS NOT NULL, ordinary TIMESTAMP)");
        var result = await new SchemaInspector().InspectAsync(source, new DuckDbMetadataReader(),
            new() { Features = [SchemaFeature.Tables, SchemaFeature.Columns] });
        Assert.That(result.Diagnostics, Is.Empty);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Schema!.Tables.Values.Single().Columns["stamp"].Type, Is.EqualTo(DbType.DateTime2));
        var document = new SchemaDocumentExporter().Map(result.Schema);
        var columns = document.Tables.Single().Columns;
        Assert.That(columns.Single(column => column.Name == "stamp").FractionalSecondsPrecision, Is.EqualTo(9));
        Assert.That(columns.Single(column => column.Name == "ordinary").FractionalSecondsPrecision, Is.Null);

        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        var schema = new SchemaDocumentMapper().Map(document);
        var script = new SchemaScriptRenderer(builder.Build().Get<DuckDbDialect>()).Render(schema);
        Assert.That(script, Does.Contain("stamp TIMESTAMP_NS NOT NULL"));

        using var target = new DuckDBConnection("DataSource=:memory:");
        target.Open();
        QaSql.Execute(target, script);
        QaSql.Execute(target, "INSERT INTO times (stamp) VALUES (TIMESTAMP_NS '2000-01-01 00:00:00.123456789')");
        Assert.That(QaSql.Rows(target, "SELECT CAST(stamp AS VARCHAR) FROM times").Single()[0],
            Is.EqualTo("2000-01-01 00:00:00.123456789"));
    }
}
