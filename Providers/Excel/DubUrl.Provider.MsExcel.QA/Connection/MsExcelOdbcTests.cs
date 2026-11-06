using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;
namespace DubUrl.Provider.MsExcel.QA.Connection;
[TestFixture, Category("MsExcel"), NonParallelizable]
public sealed class MsExcelOdbcTests : OdbcContract
{
    private static string CurrentDirectory => Path.GetDirectoryName(typeof(MsExcelOdbcTests).Assembly.Location) + "\\";
    protected override string ConnectionUrl => $"odbc+xlsx:///MsExcel/Customer.xlsx?Defaultdir={CurrentDirectory}";
    protected override string SelectFirstCustomerSql => "select [FullName] from [Customer$] where [CustomerId]=1";
    protected override string SelectCustomerByIdSql => "select [FullName] from [Customer$] where [CustomerId]=?";
}
