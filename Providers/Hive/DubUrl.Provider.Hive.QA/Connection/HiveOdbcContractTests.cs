using DubUrl.Providers.QA.Contracts.Connection;

namespace DubUrl.Provider.Hive.QA.Connection;

public sealed class HiveOdbcContractTests : OdbcContract
{
    protected override string ConnectionUrl => HiveQaDatabase.GetUrl();
    protected override string AuthenticationConnectionUrl => HiveQaDatabase.GetLocation();
    protected override string SelectFirstCustomerSql => HiveQaDatabase.FirstCustomer;
    protected override string SelectCustomerByIdSql => HiveQaDatabase.CustomerById;
}
