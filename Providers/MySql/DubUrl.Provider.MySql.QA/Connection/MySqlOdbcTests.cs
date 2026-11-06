using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;
namespace DubUrl.Provider.MySql.QA.Connection;
[TestFixture, Category("MySQL"), Category("MySQLDriver")]
public sealed class MySqlOdbcTests : OdbcContract
{
    protected override string ConnectionUrl => "odbc+mysql://root:Password12!@localhost/DubUrl";
    protected override string SelectFirstCustomerSql => "select FullName from Customer where CustomerId=1";
    protected override string SelectCustomerByIdSql => "select FullName from Customer where CustomerId=?";
}
