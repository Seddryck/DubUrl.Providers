using System.Data.Common;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.SingleStore.QA;

[Category("SingleStore")]
public sealed partial class SingleStoreSchemaTests : ProviderContract
{
    protected override DbConnection CreateConnection() => SingleStoreTestDatabase.CreateConnection();
    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SingleStoreDialect>(["singlestore"]);
        return builder.Build().Get<SingleStoreDialect>();
    }
    protected override SchemaCapabilities Capabilities => SchemaCapabilities.CreateTable | SchemaCapabilities.DropTableIfExists;
    protected override string QuoteIdentifier(string identifier) => $"`{identifier}`";
}
