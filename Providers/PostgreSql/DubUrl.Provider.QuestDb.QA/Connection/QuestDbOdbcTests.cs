using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;

namespace DubUrl.Provider.QuestDb.QA.Connection;

[TestFixture, Category("QuestDB")]
public sealed class QuestDbOdbcTests : OdbcContract
{
    protected override string ConnectionUrl => "odbc+questdb://admin:quest@localhost:8812/?TrustServerCertificate=Yes";
    protected override string CliTableName => "\"Customer\"";
    protected override string SelectFirstCustomerSql => "select \"FullName\" from \"Customer\" where \"CustomerId\"=1";
    protected override string SelectCustomerByIdSql => string.Empty;
    protected override bool SupportsParameters => false;
}
