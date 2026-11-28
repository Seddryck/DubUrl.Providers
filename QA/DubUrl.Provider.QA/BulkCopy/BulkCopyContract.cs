using System.Data;
using System.Data.Common;
using DubUrl.BulkCopy;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.BulkCopy;

[Category("BulkCopy")]
public abstract class BulkCopyContract
{
    protected abstract ConnectionUrl CreateConnectionUrl();

    [TestCase(BulkCopyTransactionMode.WholeCopy, null, 0)]
    [TestCase(BulkCopyTransactionMode.PerBatch, 2, 4)]
    [TestCase(BulkCopyTransactionMode.PerBatch, 3, 3)]
    public void TransactionModeControlsWhatRemainsAfterAFailedBatch(BulkCopyTransactionMode mode, int? batchSize, int committedRows)
    {
        var url = CreateConnectionUrl();
        var table = $"DubUrlBulk_{Guid.NewGuid():N}";
        using var db = url.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"CREATE TABLE {table} (id INTEGER NOT NULL PRIMARY KEY)";
        command.ExecuteNonQuery();
        try
        {
            command.CommandText = $"INSERT INTO {table} (id) VALUES (99)";
            command.ExecuteNonQuery();

            using var input = new DataTable();
            input.Columns.Add("id", typeof(int));
            foreach (var id in new[] { 1, 2, 3, 4, 99, 5 })
                input.Rows.Add(id);
            using var reader = input.CreateDataReader();

            Assert.Catch<DbException>(() => url.BulkCopy(table, reader,
                new BulkCopyOptions { TransactionMode = mode, BatchSize = batchSize }));
            Assert.That(reader.IsClosed, Is.False);

            command.CommandText = $"SELECT id FROM {table} ORDER BY id";
            using var output = command.ExecuteReader();
            var actual = new List<int>();
            while (output.Read())
                actual.Add(output.GetInt32(0));
            Assert.That(actual, Is.EqualTo(Enumerable.Range(1, committedRows).Append(99)));
        }
        finally
        {
            command.CommandText = $"DROP TABLE {table}";
            command.ExecuteNonQuery();
        }
    }

    [TestCase(BulkCopyTransactionMode.ProviderDefault, null)]
    [TestCase(BulkCopyTransactionMode.WholeCopy, null)]
    [TestCase(BulkCopyTransactionMode.PerBatch, null)]
    [TestCase(BulkCopyTransactionMode.PerBatch, 2)]
    public void CopiesRowsAndNulls(BulkCopyTransactionMode mode, int? batchSize)
    {
        var url = CreateConnectionUrl();
        var table = $"DubUrlBulk_{Guid.NewGuid():N}";
        using var db = url.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"CREATE TABLE {table} (id INTEGER NOT NULL, amount DECIMAL(10,2) NULL)";
        command.ExecuteNonQuery();
        try
        {
            using var input = new DataTable();
            input.Columns.Add("id", typeof(int));
            input.Columns.Add("amount", typeof(decimal));
            input.Rows.Add(1, 10.20m);
            input.Rows.Add(2, DBNull.Value);
            input.Rows.Add(3, 1500m);
            using var reader = input.CreateDataReader();

            url.BulkCopy(table, reader, new BulkCopyOptions { TransactionMode = mode, BatchSize = batchSize });

            Assert.That(reader.IsClosed, Is.False);
            command.CommandText = $"SELECT id, amount FROM {table} ORDER BY id";
            using var output = command.ExecuteReader();
            Assert.That(output.Read(), Is.True);
            Assert.That(output.GetInt32(0), Is.EqualTo(1));
            Assert.That(output.GetDecimal(1), Is.EqualTo(10.20m));
            Assert.That(output.Read(), Is.True);
            Assert.That(output.GetInt32(0), Is.EqualTo(2));
            Assert.That(output.IsDBNull(1), Is.True);
            Assert.That(output.Read(), Is.True);
            Assert.That(output.GetInt32(0), Is.EqualTo(3));
            Assert.That(output.GetDecimal(1), Is.EqualTo(1500m));
            Assert.That(output.Read(), Is.False);
        }
        finally
        {
            command.CommandText = $"DROP TABLE {table}";
            command.ExecuteNonQuery();
        }
    }
}
