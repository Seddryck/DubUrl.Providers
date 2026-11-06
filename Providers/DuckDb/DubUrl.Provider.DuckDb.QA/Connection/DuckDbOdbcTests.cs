using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;

namespace DubUrl.Provider.DuckDb.QA.Connection;

[TestFixture, Category("DuckDB"), NonParallelizable]
public sealed class DuckDbOdbcTests : OdbcContract
{
    protected override string ConnectionUrl => DuckDbTestDatabase.ConnectionUrl.Replace("duckdb:", "odbc+duckdb:");
    protected override string SelectFirstCustomerSql => "select \"FullName\" from \"Customer\" where \"CustomerId\"=1";
    protected override string SelectCustomerByIdSql => "select \"FullName\" from \"Customer\" where \"CustomerId\"=?";
}
