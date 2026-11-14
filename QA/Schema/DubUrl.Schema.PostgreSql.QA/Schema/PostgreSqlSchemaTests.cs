using DubUrl.Provider.PostgreSql.Sql;
using System.Data.Common;
using DubUrl.Querying.Dialects;
using DubUrl.Providers.QA.Contracts.Schema;
using Npgsql;
using NUnit.Framework;

namespace DubUrl.Provider.PostgreSql.QA;

[TestFixture]
[Category("Postgresql")]
public sealed partial class PostgreSqlSchemaTests : ProviderContract
{
    private Version serverVersion = null!;

    [OneTimeSetUp]
    public void ReadServerVersion()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        connection.Open();
        serverVersion = connection.PostgreSqlVersion;
        featureExpectations = CreateFeatureExpectations(serverVersion.Major);
    }

    protected override DbConnection CreateConnection()
        => new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);

    protected override IDialect CreateDialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["pgsql"]);
        return builder.Build().Get<PgsqlDialect>().WithServerVersion(serverVersion);
    }

    protected override SchemaCapabilities Capabilities
        => SchemaCapabilities.CreateTable | SchemaCapabilities.DropTableIfExists;
}
