using System.Data.Common;
using DubUrl.Provider.Oracle.QA;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.Oracle.QA;

[TestFixture]
[Category("Oracle")]
public sealed partial class OracleBaselineTests : ProviderContract
{
    private OracleTestSchema schema = null!;

    [OneTimeSetUp]
    public void CreateSchema() => schema = OracleTestDatabase.CreateSchema();

    [OneTimeTearDown]
    public void DropSchema() => schema?.Dispose();

    protected override DbConnection CreateConnection() => schema.Connect();

    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<OracleDialect>(["oracle"]);
        return builder.Build().Get<OracleDialect>();
    }

    protected override SchemaCapabilities Capabilities
        => SchemaCapabilities.CreateTable;
    // ODP.NET executes SQL statements without the SQL*Plus terminator.
    protected override string PrepareCommandText(string script) => script.Trim().TrimEnd(';');
}
