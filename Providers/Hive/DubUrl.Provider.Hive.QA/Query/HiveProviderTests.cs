using DubUrl.Provider.Hive.QA.Connection;
using DubUrl.Providers.QA.Contracts.Query;

namespace DubUrl.Provider.Hive.QA.Query;

public sealed class HiveProviderTests : ProviderContract
{
    protected override string ConnectionUrl => HiveQaDatabase.GetUrl();
    protected override string AuthenticationConnectionUrl => HiveQaDatabase.GetLocation();
    protected override string SelectFirstCustomerSql => HiveQaDatabase.FirstCustomer;
    protected override string SelectCustomerByIdSql => HiveQaDatabase.CustomerById;
    protected override string SelectCustomerByPositionSql => HiveQaDatabase.CustomerById;
    // The driver preserves explicit SQL aliases in translation mode. MicroOrm binds those labels by case.
    protected override string SelectAllCustomersSql => "select CustomerId as CustomerId, FullName as FullName, BirthDate as BirthDate from Customer";
    protected override string SelectYoungestCustomersSql => SelectAllCustomersSql + " order by BirthDate desc limit $count$";
    protected override string SelectWhereCustomersTemplate => $$"""
        {{SelectAllCustomersSql}}
        where $clauses:{clause | $clause.Field$ $clause.Operator$ $clause.Value; format="value"$}; separator=" and "$
        """;

    protected override bool SupportsNamedParameters => false; // ODBC uses positional ? markers.
    protected override bool SupportsTime => false; // HiveServer2 4.0.1 has no standalone TIME type.
    protected override bool SupportsInterval => false; // Hive's interval literal and returned text differ from the shared TimeSpan mapping.
}
