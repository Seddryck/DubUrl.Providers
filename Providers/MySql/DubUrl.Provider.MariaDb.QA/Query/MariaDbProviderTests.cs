using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;
namespace DubUrl.Provider.MariaDb.QA.Query;
[TestFixture, Category("MariaDB")]
public sealed class MariaDbProviderTests : ProviderContract
{
    protected override string ConnectionUrl => MariaDbTestDatabase.ConnectionUrl;
    protected override bool SupportsSchemaDiscovery => true;
    protected override string SelectFirstCustomerSql => "select FullName from Customer where CustomerId=1";
    protected override string SelectCustomerByIdSql => "select FullName from Customer where CustomerId=@CustId";
    protected override string SelectCustomerByPositionSql => "select FullName from Customer where CustomerId=?";
    protected override string SelectAllCustomersSql => "select * from Customer";
    protected override string SelectYoungestCustomersSql => "select CustomerId, FullName, BirthDate from Customer order by BirthDate desc limit $count$";
    protected override string SelectWhereCustomersTemplate => """
        select $fields:{field | `$field$`}; separator=", "$ from `Customer`
        where $clauses:{clause | `$clause.Field$` $clause.Operator$ $clause.Value;format="value"$}; separator=" and "$
        """;
}
