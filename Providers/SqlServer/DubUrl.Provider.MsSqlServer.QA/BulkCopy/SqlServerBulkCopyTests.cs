using DubUrl.Providers.QA.Contracts.BulkCopy;
using NUnit.Framework;

namespace DubUrl.Provider.MsSqlServer.QA.BulkCopy;

[TestFixture, Category("MsSqlServer")]
public sealed class SqlServerBulkCopyTests : BulkCopyContract
{
    protected override ConnectionUrl CreateConnectionUrl() => new(MsSqlServerProviderTests.DatabaseUrl);
}
