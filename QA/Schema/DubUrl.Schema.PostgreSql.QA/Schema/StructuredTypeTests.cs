using System.Data;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.QA;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using Npgsql;
using NUnit.Framework;

namespace DubUrl.Schema.PostgreSql.QA;

[TestFixture]
[Category("PostgreSql")]
[Category("Schema")]
public sealed class StructuredTypeTests
{
    [Test]
    public async Task NativeArrayRoundTrips()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["postgresql"]);
        var type = new StructuredColumnType(new ArrayType(new ScalarType(DbType.Int32), true), StructuredStorage.Native);
        await StructuredTypeChecks.VerifyAsync(connection, builder.Build().Get<PgsqlDialect>(),
            new PostgreSqlMetadataReader(), new("structured_array_values", "public"), type,
            "to_json(v)::text", "ARRAY[1,NULL,3]", "[1,null,3]",
            "'{}'", "[]", invalid: "ARRAY[[1,2]]");
    }

    [Test]
    public async Task JsonRoundTrips()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["postgresql"]);
        await StructuredTypeChecks.VerifyJsonAsync(connection, builder.Build().Get<PgsqlDialect>(),
            new PostgreSqlMetadataReader(), new("structured_json_values", "public"));
    }
}
