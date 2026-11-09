using Microsoft.Data.SqlClient;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.SqlServer.QA;

[TestFixture]
[Category("SqlServer")]
[Category("Schema")]
public sealed class UniqueNullPolicyTests
{
    [TestCase(UniqueNullPolicy.NullsDistinct)]
    [TestCase(UniqueNullPolicy.NullsNotDistinct)]
    public async Task CompositeTupleEnforcementAndDiscovery(UniqueNullPolicy policy)
    {
        using var connection = new SqlConnection(Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable SQL Server QA database."));
        await connection.OpenAsync();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssql"]);
        await UniqueNullPolicyChecks.VerifyAsync(connection, builder.Build().Get<TSqlDialect>(), new SqlServerMetadataReader(),
            new(policy, Schema: "dbo", UsesIndex: policy == UniqueNullPolicy.NullsDistinct,
                CanReference: policy != UniqueNullPolicy.NullsDistinct),
            exception => exception is SqlException { Number: 2601 or 2627 });
    }
}
