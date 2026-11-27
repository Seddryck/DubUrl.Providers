using DubUrl.BulkCopy;
using System.Data;
using Microsoft.Data.SqlClient;

namespace DubUrl.Provider.SqlServer.SqlClient.BulkCopy;

public sealed class MsSqlServerBulkCopyProvider : IBulkCopyProvider
{
    public IBulkCopyEngine Create(ConnectionUrl connectionUrl, IBulkCopyDependencyResolver dependencies)
        => new Engine(connectionUrl, dependencies);

    internal static SqlBulkCopyOptions GetSqlClientOptions(BulkCopyOptions options)
    {
        var nativeOptions = options.TableLock ? SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.Default;
        if (options.TransactionMode == BulkCopyTransactionMode.PerBatch)
            nativeOptions |= SqlBulkCopyOptions.UseInternalTransaction;
        return nativeOptions;
    }

    internal static SqlBulkCopy CreateBulkCopy(SqlConnection connection, string tableName,
        BulkCopyOptions options = default, SqlTransaction? transaction = null)
        => new(connection, GetSqlClientOptions(options), transaction)
        {
            DestinationTableName = tableName,
            BatchSize = options.BatchSize ?? BulkCopyOptions.DefaultBatchSize,
            EnableStreaming = true,
            BulkCopyTimeout = 0
        };

    private sealed class Engine(ConnectionUrl connectionUrl, IBulkCopyDependencyResolver dependencies) : IBulkCopyEngine
    {
        public void Write(string tableName, IDataReader dataReader)
            => Write(tableName, dataReader, default);

        public void Write(string tableName, IDataReader dataReader, BulkCopyOptions options)
        {
            options.Validate();
            using var connection = connectionUrl.Open();
            dependencies.Execute(connection.GetType().Assembly, () =>
            {
                if (connection is not SqlConnection sqlConnection)
                    throw new NotSupportedException("The SQL Server bulk-copy adapter requires a Microsoft.Data.SqlClient connection.");
                using var transaction = options.TransactionMode == BulkCopyTransactionMode.WholeCopy
                    ? sqlConnection.BeginTransaction()
                    : null;
                using var bulkCopy = CreateBulkCopy(sqlConnection, tableName, options, transaction);
                if (options.TransactionMode == BulkCopyTransactionMode.PerBatch)
                {
                    using var batchReader = new BatchDataReader(dataReader, bulkCopy.BatchSize);
                    while (batchReader.StartBatch())
                        bulkCopy.WriteToServer(batchReader);
                }
                else
                {
                    bulkCopy.WriteToServer(dataReader);
                }

                transaction?.Commit();
            });
        }

        public void Dispose() { }
    }
}
