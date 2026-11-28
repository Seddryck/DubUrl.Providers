using System.Reflection;
using DubUrl.BulkCopy;
using DubUrl.Core.ProviderPacks;
using DubUrl.Mapping;
using DubUrl.Providers.QA.Contracts.BulkCopy;
using NUnit.Framework;

namespace DubUrl.Provider.MsSqlServer.QA.BulkCopy;

[TestFixture, Category("MsSqlServer")]
public sealed class SqlServerBulkCopyTests : BulkCopyContract
{
    private static readonly ISchemeRegistry Registry = new ProviderCatalogBuilder()
        .AddModule(AttributeProviderModule.FromConnector(new ConnectorDefinition
        {
            Provider = "sqlserver-sqlclient",
            Version = "0.0.0",
            DubUrlApiRange = "*",
            Assembly = "DubUrl.Provider.SqlServer.dll",
            ImplementationAssemblies = ["DubUrl.Provider.SqlServer.dll"],
            Driver = "Microsoft.Data.SqlClient",
            Databases = [new("Microsoft SQL Server", ["mssql", "ms", "sqlserver", "mssqlserver"])],
            TargetFrameworks = [$"net{Environment.Version.Major}.0"],
            RuntimeIdentifiers = []
        }, path => Assembly.Load(Path.GetFileNameWithoutExtension(path))))
        .Build();

    protected override ConnectionUrl CreateConnectionUrl()
        => new(Environment.GetEnvironmentVariable("DUBURL_MSSQLSERVER_BULK_COPY_QA_URL") ?? MsSqlServerProviderTests.DatabaseUrl,
            Registry);

    [Test]
    public void PerBatchPreservesSqlDecimalPrecisionWhenCopyingFromASqlDataReader()
    {
        const string value = "12345678901234567890123456789012345678";
        var url = CreateConnectionUrl();
        var table = $"DubUrlBulk_{Guid.NewGuid():N}";
        using var db = url.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"CREATE TABLE {table} (amount DECIMAL(38,0) NOT NULL)";
        command.ExecuteNonQuery();
        try
        {
            command.CommandText = $"SELECT CAST('{value}' AS DECIMAL(38,0)) AS amount";
            using (var reader = command.ExecuteReader())
                url.BulkCopy(table, reader, new BulkCopyOptions
                {
                    TransactionMode = BulkCopyTransactionMode.PerBatch,
                    BatchSize = 1
                });

            command.CommandText = $"SELECT CAST(amount AS VARCHAR(38)) FROM {table}";
            Assert.That(command.ExecuteScalar(), Is.EqualTo(value));
        }
        finally
        {
            command.CommandText = $"DROP TABLE {table}";
            command.ExecuteNonQuery();
        }
    }
}
