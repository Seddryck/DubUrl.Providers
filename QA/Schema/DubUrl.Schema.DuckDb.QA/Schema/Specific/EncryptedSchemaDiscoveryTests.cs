using System.Data.Common;
using DubUrl.Authentication;
using DubUrl.Discovery;
using DubUrl.Mapping;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Mapping;
using DubUrl.Providers.QA.Contracts.Schema;
using DuckDB.NET.Data;
using NUnit.Framework;

namespace DubUrl.Schema.DuckDb.QA;

[TestFixture]
[NonParallelizable]
[Category("DuckDb")]
[Category("Schema")]
[Category("Authentication")]
public sealed class EncryptedSchemaDiscoveryTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task InspectEncryptedCatalog_ExcludesMemoryObjects(bool useAuthenticationStrategy)
    {
        var path = Path.Combine(Path.GetTempPath(), "duburl-schema-encrypted-' %#+-" + Guid.NewGuid().ToString("N") + ".duckdb");
        var key = Guid.NewGuid().ToString("N") + "%2B +&'\";--";
        var hadFactory = DbProviderFactories.TryGetFactory("DuckDB.NET.Data", out var previous);
        DbProviderFactories.RegisterFactory("DuckDB.NET.Data", DuckDBClientFactory.Instance);
        try
        {
            using (var setup = new DuckDBConnection("DataSource=:memory:"))
            {
                setup.Open();
                QaSql.Execute(setup, "ATTACH " + QaSql.Literal(path) + " AS secured (ENCRYPTION_KEY " + QaSql.Literal(key) + "); USE secured");
                QaSql.Execute(setup, "CREATE TABLE sample(id INTEGER PRIMARY KEY, title VARCHAR); CREATE INDEX ix_title ON sample(title); CREATE VIEW sample_view AS SELECT id,title FROM sample");
            }

            var builder = new ProviderCatalogBuilder();
            builder.AddModule(new AttributeProviderModule(typeof(DuckdbMapper).Assembly));
            var url = "duckdb:///" + path.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23").Replace(" ", "%20");
            var target = useAuthenticationStrategy
                ? new ConnectionUrl(url, builder.Build()).WithAuthentication(ConnectionAuthentication.FilePassword(key))
                : new ConnectionUrl(url + "?ENCRYPTION_KEY=" + Uri.EscapeDataString(key), builder.Build());
            using var connection = (DbConnection)target.Open();
            QaSql.Execute(connection, "CREATE TABLE memory.main.decoy(id INTEGER)");

            var discovery = await new SchemaInspector().InspectAsync(connection, new DuckDbMetadataReader(), new DiscoveryOptions
            {
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.PrimaryKeys, SchemaFeature.Indexes, SchemaFeature.Views]
            });
            Assert.That(discovery.IsSuccess && discovery.IsComplete, Is.True);
            Assert.That(discovery.Schema!.Tables.Values.Select(t => t.Name), Is.EqualTo(new[] { "sample" }));
            Assert.That(discovery.Schema.Indexes.Values.Any(i => i.Name == "ix_title"), Is.True);
            Assert.That(discovery.ViewInventory, Has.Count.EqualTo(1));
        }
        finally
        {
            if (hadFactory) DbProviderFactories.RegisterFactory("DuckDB.NET.Data", previous!);
            else DbProviderFactories.UnregisterFactory("DuckDB.NET.Data");
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".wal")) File.Delete(path + ".wal");
        }
    }
}
