using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.Oracle.QA;

[TestFixture]
[Category("Oracle")]
public sealed partial class OracleBaselineTests : ProviderContract
{
    protected override DbConnection CreateConnection()
        => new OracleConnection(Environment.GetEnvironmentVariable("DUBURL_ORACLE_QA_CONNECTION_STRING") ?? throw new InvalidOperationException("Configure a disposable Oracle QA database."));

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
