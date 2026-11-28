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
    public void SqlClient_ComposesTableLockAndPerBatchOptions()
    {
        var options = new BulkCopyOptions
        {
            TableLock = true,
            TransactionMode = BulkCopyTransactionMode.PerBatch,
            BatchSize = 3
        };
        using var connection = new SqlConnection();
        using var bulkCopy = MsSqlServerBulkCopyProvider.CreateBulkCopy(connection, "dbo.Customer", options);

        Assert.Multiple(() =>
        {
            Assert.That(MsSqlServerBulkCopyProvider.GetSqlClientOptions(options),
                Is.EqualTo(SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.UseInternalTransaction));
            Assert.That(bulkCopy.BatchSize, Is.EqualTo(3));
        });
    }

    [Test]
    public void SqlClient_BatchReaderDoesNotReadAcrossTheCommitBoundary()
    {
        var source = new Mock<IDataReader>(MockBehavior.Strict);
        source.SetupSequence(x => x.Read()).Returns(true).Returns(true)
            .Throws(new InvalidOperationException("Reader failed at the next batch."));
        using (var reader = new BatchDataReader(source.Object, 2))
        {
            Assert.That(reader.StartBatch(), Is.True);
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.Read(), Is.False);
            source.Verify(x => x.Read(), Times.Exactly(2));

            Assert.That(() => reader.StartBatch(),
                Throws.InvalidOperationException.With.Message.EqualTo("Reader failed at the next batch."));
        }
        source.Verify(x => x.Read(), Times.Exactly(3));
        source.VerifyNoOtherCalls();
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
