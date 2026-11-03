using DubUrl.BulkCopy;
using System.Data;
using DubUrl.Mapping;
using DubUrl.Querying.Dialects;
using Moq;

namespace DubUrl.Provider.DuckDb.QA.BulkCopy.Specific;

internal static class BulkCopyQaSupport
{
    internal static IDialect GetDialect<T>() where T : IDialect
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<T>(["test"]);
        return builder.Build().Get<T>();
    }
    internal sealed class TestConnectionUrl(IDialect dialect, Func<IDbConnection>? open = null, IBulkCopyProvider? bulkCopyProvider = null)
        : ConnectionUrl("test://localhost/db", Mock.Of<ISchemeRegistry>())
    {
        public override IDialect Dialect => dialect;
        public override IBulkCopyProvider? BulkCopyProvider => bulkCopyProvider;
        public override IDbConnection Open() => open?.Invoke() ?? throw new InvalidOperationException("No database should be opened.");
    }
}
