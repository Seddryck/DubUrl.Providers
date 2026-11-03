using DubUrl.Provider.DuckDb.Sql;
using System.Data;
using System.Reflection;
using DubUrl.Provider.DuckDb.Native.BulkCopy;
using DubUrl.Provider.SqlServer.SqlClient.BulkCopy;
using DubUrl.Mapping;
using DubUrl.Querying.Dialects;
using DuckDB.NET.Data;
using Moq;
using NUnit.Framework;
using static DubUrl.BulkCopy.Testing.BulkCopyEngineFactoryTests;

namespace DubUrl.BulkCopy.Testing;

public class NativeBulkCopyIntegrationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void NativeEngine_FailureDisposesDependencyScopeAndConnection(bool duckDb)
    {
        var connection = new Mock<IDbConnection>();
        var scope = new Mock<IDisposable>();
        var dependencies = new Mock<IBulkCopyDependencyResolver>();
        dependencies.Setup(x => x.EnterScope(connection.Object.GetType().Assembly)).Returns(scope.Object);
        var url = new TestConnectionUrl(GetDialect<DuckDbDialect>(), () => connection.Object);
        var reader = new Mock<IDataReader>();
        IBulkCopyProvider provider = duckDb ? new DuckDbBulkCopyProvider() : new MsSqlServerBulkCopyProvider();
        var engine = provider.Create(url, dependencies.Object);
        using (engine)
            Assert.Throws<NotSupportedException>(() => engine.Write("Customer", reader.Object));

        scope.Verify(x => x.Dispose(), Times.Once);
        connection.Verify(x => x.Dispose(), Times.Once);
        reader.Verify(x => x.Dispose(), Times.Never);
    }
}
