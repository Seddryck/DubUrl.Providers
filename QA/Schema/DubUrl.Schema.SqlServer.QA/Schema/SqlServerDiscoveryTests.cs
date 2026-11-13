using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace DubUrl.Provider.MsSqlServer.QA;

public sealed class SqlServerDiscoveryTests : DiscoveryContract
{
    protected override DbConnection? CreateConfiguredConnection() => Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING") is { Length: > 0 } value ? new SqlConnection(value) : null;
    protected override ISchemaMetadataReader CreateReader() => new SqlServerMetadataReader();
    protected override bool IsSqlServer => true;

    [Test]
    [Category("Discovery")]
    public async Task Discovery_AttributesUnsupportedCatalogDefinitionsToTheSelectedTable()
    {
        await using var connection = CreateConfiguredConnection();
        if (connection is null)
        {
            Assert.Ignore("Configure a disposable SQL Server discovery QA database to run this fixture.");
            return;
        }
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var schema = "diagnostics_" + suffix;
        var table = $"[{schema}].[sample]";
        await using var command = connection.CreateCommand();
        var defaultName = "DF_ordered_" + suffix;
        var checkName = "CK_amount_" + suffix;
        var options = new DiscoveryOptions
        {
            Schemas = [schema],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Defaults, SchemaFeature.Generation,
                SchemaFeature.Indexes, SchemaFeature.Checks],
        };
        try
        {
            command.CommandText = $"CREATE SCHEMA [{schema}]";
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"""
                CREATE TABLE {table} (id int NOT NULL, node hierarchyid, notes nvarchar(max), amount int,
                    ordered_at datetime CONSTRAINT [{defaultName}] DEFAULT (getdate()),
                    total AS (amount + id),
                    CONSTRAINT [{checkName}] CHECK (amount > 0 OR amount IS NULL));
                CREATE INDEX IX_sample ON {table}(id) INCLUDE (amount) WHERE id > 0;
                """;
            await command.ExecuteNonQueryAsync();

            var result = await new DubUrl.Schema.SchemaInspector().InspectAsync(connection, CreateReader(), options);
            Assert.That(result.IsComplete, Is.False);
            var diagnostics = result.Diagnostics.Where(item => item.Code == "SDI009").ToArray();
            Assert.That(diagnostics, Has.Length.EqualTo(6));
            Assert.That(diagnostics.Select(item => item.Object), Is.All.EqualTo(new MetadataName("sample", schema)));
            var filtered = await new DubUrl.Schema.SchemaInspector().InspectAsync(connection, CreateReader(),
                options with { Schemas = [], Tables = [new("missing", schema)] });
            Assert.That(filtered.Diagnostics.Select(item => item.Code), Does.Not.Contain("SDI009"));
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS {table}; IF SCHEMA_ID(N'{schema}') IS NOT NULL DROP SCHEMA [{schema}];";
            await command.ExecuteNonQueryAsync();
        }
    }

    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssql"]);
        return builder.Build().Get<TSqlDialect>();
    }
}
