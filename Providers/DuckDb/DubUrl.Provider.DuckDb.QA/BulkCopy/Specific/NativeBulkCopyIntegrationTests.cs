using DubUrl.BulkCopy;
using DubUrl.Provider.DuckDb.Sql;
using System.Data;
using System.Reflection;
using DubUrl.Provider.DuckDb.Native.BulkCopy;
using DubUrl.Mapping;
using DubUrl.Querying.Dialects;
using DuckDB.NET.Data;
using Moq;
using NUnit.Framework;
using static DubUrl.Provider.DuckDb.QA.BulkCopy.Specific.BulkCopyQaSupport;

namespace DubUrl.Provider.DuckDb.QA.BulkCopy.Specific;

[Category("BulkCopy")]
public class NativeBulkCopyIntegrationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void DuckDbFacade_CopiesRowsThroughTypedConnector(bool includeDateTime)
    {
        var path = Path.Combine(Path.GetTempPath(), "DubUrl-bulk-" + Guid.NewGuid().ToString("N") + ".db");
        DuckDBConnection Open()
        {
            var connection = new DuckDBConnection($"DataSource={path}");
            connection.Open();
            return connection;
        }
        try
        {
            using (var setup = Open())
            using (var command = setup.CreateCommand())
            {
                command.CommandText = "CREATE TABLE Customer (id INTEGER, name VARCHAR" +
                    (includeDateTime ? ", birth DATE, wake TIME" : string.Empty) + ")";
                command.ExecuteNonQuery();
            }
            using var data = new DataTable();
            data.Columns.Add("id", typeof(int));
            data.Columns.Add("name", typeof(string));
            if (includeDateTime)
            {
                data.Columns.Add("birth", typeof(DateOnly));
                data.Columns.Add("wake", typeof(TimeOnly));
                data.Rows.Add(1, "owner's", new DateOnly(2021, 1, 1), new TimeOnly(15, 18, 10));
                data.Rows.Add(2, "second", new DateOnly(2022, 2, 2), new TimeOnly(9, 30));
            }
            else
            {
                data.Rows.Add(1, "owner's");
                data.Rows.Add(2, "second");
            }
            var dependencies = new Mock<IBulkCopyDependencyResolver>();
            dependencies.Setup(x => x.EnterScope(It.IsAny<Assembly>()))
                .Returns((Assembly driver) => BulkCopyDependencyResolver.Default.EnterScope(driver));
            dependencies.Setup(x => x.ResolveType(It.IsAny<Assembly>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns((Assembly driver, string assembly, string type) => BulkCopyDependencyResolver.Default.ResolveType(driver, assembly, type));
            var registry = new ProviderCatalogBuilder()
                .WithRootPath(Path.GetDirectoryName(path)!)
                .AddModule(ConnectorFixture.Create())
                .Build();
            var url = new ConnectionUrl("duck://" + Path.GetFileName(path), registry);
            using var reader = data.CreateDataReader();

            url.BulkCopy("Customer", reader, dependencies.Object);

            Assert.That(reader.IsClosed, Is.False);
            dependencies.Verify(x => x.ResolveType(It.IsAny<Assembly>(), "DubUrl.Provider.DuckDb.Native.BulkCopy",
                typeof(DuckDbBulkCopyProvider).FullName!), Times.Once);
            using var connection = Open();
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT id, name" +
                (includeDateTime ? ", CAST(birth AS VARCHAR), CAST(wake AS VARCHAR)" : string.Empty) + " FROM Customer ORDER BY id";
            using var result = select.ExecuteReader();
            Assert.That(result.Read(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(result.GetInt32(0), Is.EqualTo(1));
                Assert.That(result.GetString(1), Is.EqualTo("owner's"));
                if (includeDateTime)
                {
                    Assert.That(result.GetString(2), Is.EqualTo("2021-01-01"));
                    Assert.That(result.GetString(3), Is.EqualTo("15:18:10"));
                }
            });
            Assert.That(result.Read(), Is.True);
            Assert.That(result.GetInt32(0), Is.EqualTo(2));
            Assert.That(result.Read(), Is.False);
        }
        finally { File.Delete(path); }
    }

}
