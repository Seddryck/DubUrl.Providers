using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;

namespace DubUrl.Provider.Timescale.QA.Connection;

[TestFixture, Category("Timescale")]
public sealed class TimescaleOdbcTests : OdbcContract
{
    protected override string ConnectionUrl => "odbc+ts://postgres:Password12!@localhost/DubUrl";
    protected override string CliTableName => "\"Customer\"";
    protected override string SelectFirstCustomerSql => "select \"FullName\" from \"Customer\" where \"CustomerId\"=1";
    protected override string SelectCustomerByIdSql => "select \"FullName\" from \"Customer\" where \"CustomerId\"=?";
}
