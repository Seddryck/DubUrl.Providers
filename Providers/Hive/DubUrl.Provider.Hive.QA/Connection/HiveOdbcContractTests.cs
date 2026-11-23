using DubUrl.Providers.QA.Contracts.Connection;

namespace DubUrl.Provider.Hive.QA.Connection;

public sealed class HiveOdbcContractTests : OdbcContract
{
    protected override bool SupportsSchemaDiscovery => true;
    protected override Task VerifySchemaDiscoveryAsync(ConnectionUrl url)
        => HiveQaDatabase.VerifySharedDiscoveryAsync(scope => VerifySchemaDiscoveryAsync(url, scope));

    protected override string ConnectionUrl => HiveQaDatabase.GetUrl();
    protected override string AuthenticationConnectionUrl => HiveQaDatabase.GetLocation();
    protected override string SelectFirstCustomerSql => HiveQaDatabase.FirstCustomer;
    protected override string SelectCustomerByIdSql => HiveQaDatabase.CustomerById;
}
