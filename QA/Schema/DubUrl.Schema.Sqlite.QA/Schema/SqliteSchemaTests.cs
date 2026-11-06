using DubUrl.Provider.Sqlite.Sql;
using System.Data.Common;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace DubUrl.Provider.Sqlite.QA;

[TestFixture]
[Category("Sqlite")]
public sealed partial class SqliteSchemaTests : ProviderContract
{
    protected override DbConnection CreateConnection()
    {
        SqliteTestDatabase.EnsureInitialized();
        return new SqliteConnection(SqliteTestDatabase.ProviderConnectionString + ";Foreign Keys=True");
    }

    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SqliteDialect>(["sqlite"]);
        return builder.Build().Get<SqliteDialect>();
    }

    protected override SchemaCapabilities Capabilities
        => SchemaCapabilities.CreateTable | SchemaCapabilities.DropTableIfExists;
}
