using System.Data.Common;
using Microsoft.Data.SqlClient;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.SqlServer.QA;

[TestFixture]
[Category("SqlServer")]
public sealed partial class SqlServerBaselineTests : ProviderContract
{
    protected override DbConnection CreateConnection()
        => new SqlConnection(Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING") ?? throw new InvalidOperationException("Configure a disposable SQL Server QA database."));

    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssql"]);
        return builder.Build().Get<TSqlDialect>();
    }

    protected override SchemaCapabilities Capabilities
        => SchemaCapabilities.CreateTable | SchemaCapabilities.DropTableIfExists;
    protected override string QuoteIdentifier(string identifier) => $"[{identifier}]";
}
