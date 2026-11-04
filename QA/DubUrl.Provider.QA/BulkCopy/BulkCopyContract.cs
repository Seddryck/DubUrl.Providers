using System.Data;
using DubUrl.BulkCopy;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.BulkCopy;

[Category("BulkCopy")]
public abstract class BulkCopyContract
{
    protected abstract ConnectionUrl CreateConnectionUrl();

    [Test]
    public void CopiesRowsAndNulls()
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

            url.BulkCopy(table, reader);

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
