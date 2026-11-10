using System.Data;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.SingleStore.QA;

[TestFixture]
[Category("SingleStore")]
[Category("Schema")]
public sealed class StructuredTypeTests
{
    [Test]
    public async Task VectorsRoundTrip()
    {
        using var connection = SingleStoreTestDatabase.CreateConnection();
        await connection.OpenAsync();
        var version = Version.Parse(((string)Scalar(connection, "SELECT @@memsql_version")!).Split('-')[0]);
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SingleStoreDialect>(["singlestore"]);
        var dialect = builder.Build().Get<SingleStoreDialect>().WithServerVersion(version);
        await StructuredTypeChecks.VerifyAsync(connection, dialect, new SingleStoreMetadataReader(),
            new("structured_vector_values", database: connection.Database),
            new(new VectorType(DbType.Single, 3), StructuredStorage.Native),
            "v :> JSON", "'[1,2,3]' :> VECTOR(3, F32)", "[1,2,3]", invalid: "'[1,2]' :> VECTOR(3, F32)");
    }
}
