using DubUrl.Provider.DuckDb.Sql;
using System.Data;
using System.Numerics;
using DubUrl.Provider.DuckDb.Native.BulkCopy;
using DubUrl.Provider.SqlServer.SqlClient.BulkCopy;
using DubUrl.Querying.Dialects;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Moq;
using NUnit.Framework;
using static DubUrl.BulkCopy.Testing.BulkCopyEngineFactoryTests;

namespace DubUrl.BulkCopy.Testing;

public class TypedAdapterTests
{
    [Test]
    public void SqlClient_TypedBulkCopyPreservesExistingSettings()
    {
        using var connection = new SqlConnection();
        using var bulkCopy = MsSqlServerBulkCopyProvider.CreateBulkCopy(connection, "dbo.Customer");

        Assert.Multiple(() =>
        {
            Assert.That(bulkCopy.DestinationTableName, Is.EqualTo("dbo.Customer"));
            Assert.That(bulkCopy.BatchSize, Is.EqualTo(1000));
            Assert.That(bulkCopy.EnableStreaming, Is.True);
            Assert.That(bulkCopy.BulkCopyTimeout, Is.Zero);
        });
    }

    [Test]
    public void DriverReferencesExistOnlyInOptionalAdapterAssemblies()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(DuckDbBulkCopyProvider).Assembly.GetReferencedAssemblies().Select(x => x.Name), Does.Contain("DuckDB.NET.Data"));
            Assert.That(typeof(MsSqlServerBulkCopyProvider).Assembly.GetReferencedAssemblies().Select(x => x.Name), Does.Contain("Microsoft.Data.SqlClient"));
            Assert.That(typeof(DuckDbBulkCopyProvider).Assembly.GetName().Name, Is.EqualTo("DubUrl.Provider.DuckDb.Native.BulkCopy"));
            Assert.That(typeof(MsSqlServerBulkCopyProvider).Assembly.GetName().Name, Is.EqualTo("DubUrl.Provider.SqlServer.SqlClient.BulkCopy"));
        });
    }

}
