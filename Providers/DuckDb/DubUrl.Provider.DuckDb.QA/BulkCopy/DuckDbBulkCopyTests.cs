using DubUrl.Providers.QA.Contracts.BulkCopy;
using NUnit.Framework;

namespace DubUrl.Provider.DuckDb.QA.BulkCopy;

[TestFixture, Category("DuckDB"), NonParallelizable]
public sealed class DuckDbBulkCopyTests : BulkCopyContract
{
    protected override ConnectionUrl CreateConnectionUrl() => new(DuckDbTestDatabase.ConnectionUrl);
}
