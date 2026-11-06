using System.Data.Common;
using DuckDB.NET.Data;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.DuckDb.QA;

[TestFixture]
[Category("DuckDb")]
public sealed partial class DuckDbBaselineTests : ProviderContract
{
    protected override DbConnection CreateConnection()
        => new DuckDBConnection("DataSource=:memory:");

    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        return builder.Build().Get<DuckDbDialect>();
    }

    protected override SchemaCapabilities Capabilities
        => SchemaCapabilities.CreateTable | SchemaCapabilities.DropTableIfExists;
}
