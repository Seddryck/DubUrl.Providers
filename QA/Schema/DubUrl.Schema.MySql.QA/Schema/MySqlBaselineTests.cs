using System.Data.Common;
using MySqlConnector;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.MySql.QA;

[TestFixture]
[Category("MySql")]
public sealed partial class MySqlBaselineTests : ProviderContract
{
    protected override DbConnection CreateConnection()
        => new MySqlConnection(Environment.GetEnvironmentVariable("DUBURL_SCHEMA_MYSQL") ?? throw new InvalidOperationException("Configure a disposable MySQL QA database."));

    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MySqlDialect>(["mysql"]);
        return builder.Build().Get<MySqlDialect>();
    }

    protected override SchemaCapabilities Capabilities
        => SchemaCapabilities.CreateTable | SchemaCapabilities.DropTableIfExists;
    protected override string QuoteIdentifier(string identifier) => $"`{identifier}`";
}
