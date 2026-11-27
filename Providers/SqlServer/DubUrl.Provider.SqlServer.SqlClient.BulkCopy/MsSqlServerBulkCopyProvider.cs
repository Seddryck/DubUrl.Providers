using DubUrl.BulkCopy;
using System.Data;
using Microsoft.Data.SqlClient;

namespace DubUrl.Provider.SqlServer.SqlClient.BulkCopy;

public sealed class MsSqlServerBulkCopyProvider : IBulkCopyProvider
{
    public IBulkCopyEngine Create(ConnectionUrl connectionUrl, IBulkCopyDependencyResolver dependencies)
        => new Engine(connectionUrl, dependencies);

    internal static SqlBulkCopyOptions GetSqlClientOptions(BulkCopyOptions options)
        => options.TableLock ? SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.Default;

    internal static SqlBulkCopy CreateBulkCopy(SqlConnection connection, string tableName, BulkCopyOptions options = default)
        => new(connection, GetSqlClientOptions(options), externalTransaction: null)
        {
            DestinationTableName = tableName,
            BatchSize = 1000,
            EnableStreaming = true,
            BulkCopyTimeout = 0
        };

    private sealed class Engine(ConnectionUrl connectionUrl, IBulkCopyDependencyResolver dependencies) : IBulkCopyEngine
    {
        public void Write(string tableName, IDataReader dataReader)
            => Write(tableName, dataReader, default);

        public void Write(string tableName, IDataReader dataReader, BulkCopyOptions options)
        {
            using var connection = connectionUrl.Open();
            dependencies.Execute(connection.GetType().Assembly, () =>
            {
                if (connection is not SqlConnection sqlConnection)
                    throw new NotSupportedException("The SQL Server bulk-copy adapter requires a Microsoft.Data.SqlClient connection.");
                using var bulkCopy = CreateBulkCopy(sqlConnection, tableName, options);
                bulkCopy.WriteToServer(dataReader);
            });
        }

        public void Dispose() { }
    }
}
