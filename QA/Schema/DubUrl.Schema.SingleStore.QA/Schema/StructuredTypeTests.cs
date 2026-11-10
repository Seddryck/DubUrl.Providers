using System.Data;
using DubUrl.Discovery;
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
    public async Task JsonRoundTrips()
    {
        using var connection = SingleStoreTestDatabase.CreateConnection();
        await connection.OpenAsync();
        var json = """{"Key":"café 😀","key":[1,null,{"ok":true}]}""";
        await StructuredTypeChecks.VerifyAsync(connection, Dialect(), new SingleStoreMetadataReader(),
            new("structured_json_values", database: connection.Database),
            new(new JsonType(), StructuredStorage.Json),
            "v", $"'{json}'", json, "'{}'", "{}", "'null'", "null", invalid: "'not json'");
    }

    [Test]
    public async Task DiscoveryDistinguishesNativeJsonFromText()
    {
        using var connection = SingleStoreTestDatabase.CreateConnection();
        await connection.OpenAsync();
        Execute(connection, "CREATE TABLE structured_json_catalog (v JSON, json_text LONGTEXT)");
        try
        {
            var metadata = await new SingleStoreMetadataReader().ReadWithStructuredTypesAsync(connection, new()
            {
                Tables = [new("structured_json_catalog", Database: connection.Database)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns]
            }, CancellationToken.None);
            var table = metadata.Metadata.Tables.Single();
            Assert.That(table.Columns.Single(column => column.Name == "v").NativeType, Is.EqualTo("JSON"));
            Assert.That(metadata.StructuredColumns.Keys, Is.EqualTo(new[] { new StructuredColumnIdentity(table.Identity, "v") }));
            Assert.That(metadata.StructuredColumns.Values.Single(),
                Is.EqualTo(new StructuredColumnMetadata(new(new JsonType(), StructuredStorage.Json), MetadataCoverage.Available)));
        }
        finally
        {
            Execute(connection, "DROP TABLE structured_json_catalog");
        }
    }

    [Test]
    public async Task VectorsRoundTrip()
    {
        using var connection = SingleStoreTestDatabase.CreateConnection();
        await connection.OpenAsync();
        var version = Version.Parse(((string)Scalar(connection, "SELECT @@memsql_version")!).Split('-')[0]);
        await StructuredTypeChecks.VerifyAsync(connection, Dialect().WithServerVersion(version), new SingleStoreMetadataReader(),
            new("structured_vector_values", database: connection.Database),
            new(new VectorType(DbType.Single, 3), StructuredStorage.Native),
            "v :> JSON", "'[1,2,3]' :> VECTOR(3, F32)", "[1,2,3]", invalid: "'[1,2]' :> VECTOR(3, F32)");
    }

    private static SingleStoreDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SingleStoreDialect>(["singlestore"]);
        return builder.Build().Get<SingleStoreDialect>();
    }
}
