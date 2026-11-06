using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;
namespace DubUrl.Provider.MariaDb.QA.Connection;
[TestFixture, Category("MariaDB"), Category("MariaDBDriver")]
public sealed class MariaDbOdbcTests : OdbcContract
{
    protected override string ConnectionUrl => "odbc+" + MariaDbTestDatabase.ConnectionUrl;
    protected override string SelectFirstCustomerSql => "select FullName from Customer where CustomerId=1";
    protected override string SelectCustomerByIdSql => "select FullName from Customer where CustomerId=?";
}
