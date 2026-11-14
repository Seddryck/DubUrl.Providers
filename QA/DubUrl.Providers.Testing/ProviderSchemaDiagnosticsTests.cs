using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Providers.Testing.Discovery;
using DubUrl.Schema;
using Moq;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public sealed class ProviderSchemaDiagnosticsTests
{
    [Test]
    public async Task CustomRejectionsPreserveIdentityAndFollowTheTableFilter()
    {
        var table = new MetadataName("orders", "sales", Database: "db");
        var reader = new Reader();
        var connection = new Mock<DbConnection>();
        connection.SetupGet(db => db.State).Returns(ConnectionState.Open);
        var options = new DiscoveryOptions
        {
            Tables = [table],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Defaults, SchemaFeature.Checks],
        };

        var result = await new SchemaInspector().InspectAsync(connection.Object, reader, options);

        var diagnostics = result.Diagnostics.Where(d => d.Code == "SDI009").ToArray();
        Assert.That(diagnostics, Has.Length.EqualTo(3));
        Assert.That(diagnostics.Select(d => d.Object), Is.All.EqualTo(table));
        var column = diagnostics.Single(d => d.Member == "sale price");
        Assert.That(column.Message, Does.Contain("Column \"sale price\"").And.Contain("calculate_price()"));
        Assert.That(column.NativeDefinition, Is.EqualTo("calculate_price()"));
        var check = diagnostics.Single(d => d.Member == "ck_price");
        Assert.That(check.Constraint, Is.EqualTo("ck_price"));
        Assert.That(check.NativeDefinition, Is.EqualTo("price IS DISTINCT FROM 0"));
        Assert.That(diagnostics.Single(d => d.Member is null).Message, Is.EqualTo("Legacy diagnostic."));
        Assert.That(result.IsComplete, Is.False);
    }

    private sealed class Reader : CatalogSchemaMetadataReader, ISchemaMetadataReader
    {
        protected override string TablesSql => "tables";
        protected override string ColumnsSql => "columns";
        protected override string KeysSql => "keys";
        protected override string ChecksSql => "checks";

        public new Task<SchemaMetadata> ReadAsync(DbConnection connection, DiscoveryOptions options,
            CancellationToken cancellationToken = default)
            => ProviderSchemaDiagnostics.ReadAsync(() => base.ReadAsync(connection, options, cancellationToken));

        protected override Task<List<CatalogRow>> ReadCatalogRowsAsync(DbConnection connection, string sql,
            DiscoveryOptions options, CancellationToken cancellationToken)
        {
            List<CatalogRow> rows = sql switch
            {
                "tables" => [Table("orders"), Table("other")],
                "columns" => [Column("orders"), Column("other")],
                _ => [],
            };
            return Task.FromResult(rows);

            CatalogRow Table(string name) => new(new Dictionary<string, object?> { ["database_name"] = "db", ["schema_name"] = "sales", ["table_name"] = name });
            CatalogRow Column(string name) => Table(name).With(("column_name", "sale price"), ("type_name", "int"), ("nullable", false));
        }

        protected override MetadataTable MapTable(CatalogRow row, List<CatalogRow> columns, List<CatalogRow> keys,
            List<CatalogRow> indexes, List<CatalogRow> checks, DiscoveryOptions options, Action<SchemaFeature, string> diagnose)
        {
            var table = Identity(row);
            diagnose(SchemaFeature.Defaults, "Legacy diagnostic.");
            ProviderSchemaDiagnostics.Report(diagnose, table, SchemaFeature.Defaults, "Rejected default.",
                member: "sale price", nativeDefinition: "calculate_price()");
            ProviderSchemaDiagnostics.Report(diagnose, table, SchemaFeature.Checks, "Rejected check.",
                constraint: "ck_price", nativeDefinition: "price IS DISTINCT FROM 0");
            return base.MapTable(row, columns, keys, indexes, checks, options, diagnose);
        }
    }
}
