using DubUrl.Schema;
using System.Data;
using System.Data.Common;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Querying.Dialects;
using DuckDB.NET.Data;
using NUnit.Framework;
namespace DubUrl.Provider.DuckDb.QA;

[TestFixture]
[NonParallelizable]
public sealed class DuckDbSchemaTests
{
    private string path = null!;
    [SetUp]
    public void SetUp()
    {
        DbProviderFactories.RegisterFactory("DuckDB.NET.Data", DuckDBClientFactory.Instance);
        path = Path.Combine(Path.GetTempPath(), "duburl-schema-" + Guid.NewGuid().ToString("N") + ".duckdb");
    }
    [TearDown]
    public void CleanUp()
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".wal")) File.Delete(path + ".wal");
    }
    private string ConnectionUrl => "duckdb:///" + path.Replace('\\', '/');
    [Test]
    [Category("Schema")]
    public void CreateTable()
    {
        var connection = new ConnectionUrl(ConnectionUrl);
        connection.DeploySchema(s => s.WithTables(t => t.Add(x => x.WithName("Sales").WithColumns(c => c
            .Add(y => y.WithName("SalesId").WithType(DbType.Int64))
            .Add(y => y.WithName("CustomerId").WithType(DbType.Int32))
            .Add(y => y.WithName("Amount").WithType(DbType.Decimal).WithPrecision(10).WithScale(2))
        ))), SchemaCreationOptions.DropIfExists);
        AssertTableEmpty(connection, "Sales");
    }

    [Test]
    [Category("Schema")]
    public void CreateIndex()
    {
        var connection = new ConnectionUrl(ConnectionUrl);
        connection.DeploySchema(
            schema => schema
                .WithTables(tables => tables.Add(table => table
                    .WithName("Orders")
                    .WithColumns(columns => columns
                        .Add(column => column.WithName("OrderId").WithType(DbType.Int64))
                        .Add(column => column.WithName("ProductId").WithType(DbType.Int16))
                        .Add(column => column.WithName("CustomerId").WithType(DbType.Int32)))))
                .WithIndexes(indexes => indexes.Add(index => index
                    .WithName("idx_ProductId")
                    .OnTable("Orders")
                    .WithColumns(columns => columns.Add(column => column.WithName("ProductId"))))),
            SchemaCreationOptions.DropIfExists);
        AssertTableEmpty(connection, "Orders");
    }

    private static void AssertTableEmpty(ConnectionUrl connection, string table)
    {
        using var db = connection.Open(); using var command = db.CreateCommand(); command.CommandText = $"select count(*) from {table};";
        Assert.That(command.ExecuteScalar(), Is.EqualTo(0));
    }
}
