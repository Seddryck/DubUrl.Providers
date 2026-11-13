using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using Microsoft.Data.SqlClient;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.SqlServer.QA;

[TestFixture]
[Category("SqlServer")]
[Category("Schema")]
public sealed class StructuredTypeTests
{
    [TestCase(16)]
    [TestCase(17)]
    public void NativeArraysAreRejectedBeforeDdl(int serverMajorVersion)
    {
        var dialect = Dialect(new Version(serverMajorVersion, 0));
        var identity = new DatabaseObjectName("structured_native_array", "dbo");
        var type = new StructuredColumnType(new ArrayType(new ScalarType(DbType.Int32), true), StructuredStorage.Native);

        AssertUnsupported(dialect, identity, type);
    }

    [Test]
    public async Task JsonTextRoundTripsAsUnicodeText()
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        var dialect = Dialect(connection, out _);
        var identity = new DatabaseObjectName("structured_json_text", "dbo");
        var type = new StructuredColumnType(new JsonType(), StructuredStorage.JsonText);
        var table = Create(connection, dialect, identity, new Column("id", DbType.Int32), new Column("v", type));
        try
        {
            Execute(connection, $$"""
                INSERT INTO {{table}} VALUES (1, N'{"items":["é",null]}'), (2, N'{}'), (3, NULL), (4, N'not JSON')
                """);
            Assert.That(Scalar(connection, $"SELECT v FROM {table} WHERE id=1"), Is.EqualTo("""{"items":["é",null]}"""));
            Assert.That(Scalar(connection, $"SELECT v FROM {table} WHERE id=2"), Is.EqualTo("{}"));
            Assert.That(Scalar(connection, $"SELECT v FROM {table} WHERE id=3"), Is.EqualTo(DBNull.Value));
            Assert.That(Scalar(connection, $"SELECT v FROM {table} WHERE id=4"), Is.EqualTo("not JSON"));
            var metadata = await new SqlServerMetadataReader().ReadWithStructuredTypesAsync(connection, new()
            {
                Tables = [new(identity.Name, identity.Schema)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns]
            }, CancellationToken.None);
            var catalogIdentity = new MetadataName(identity.Name, identity.Schema);
            var payload = metadata.Metadata.Tables.Single(item => item.Identity == catalogIdentity).Columns.Single(column => column.Name == "v");
            Assert.That(payload.Type, Is.EqualTo(DbType.String));
            Assert.That(metadata.StructuredColumns.ContainsKey(new(catalogIdentity, "v")), Is.False);
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }
    }

    [Test]
    public async Task NativeJsonIsQualifiedByVersion()
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        var dialect = Dialect(connection, out var version);
        var identity = new DatabaseObjectName("structured_native_json", "dbo");
        var type = new StructuredColumnType(new JsonType(), StructuredStorage.Json);
        if (version.Major < 17)
        {
            AssertUnsupported(dialect, identity, type);
            return;
        }

        await StructuredTypeChecks.VerifyAsync(connection, dialect, new SqlServerMetadataReader(), identity,
            type, "CAST(v AS NVARCHAR(MAX))", """N'{"items":[1,null,{"ok":true}]}'""",
            """{"items":[1,null,{"ok":true}]}""", "N'{}'", "{}", "N'[]'", "[]", invalid: "N'null'");
    }

    [Test]
    public async Task NativeVectorsAreQualifiedByVersion()
    {
        await using var connection = Connection();
        await connection.OpenAsync();
        var dialect = Dialect(connection, out var version);
        var identity = new DatabaseObjectName("structured_native_vector", "dbo");
        var type = new StructuredColumnType(new VectorType(DbType.Single, 3), StructuredStorage.Native);
        if (version.Major < 17)
        {
            AssertUnsupported(dialect, identity, type);
            return;
        }

        await StructuredTypeChecks.VerifyAsync(connection, dialect, new SqlServerMetadataReader(), identity,
            type, "CAST(v AS NVARCHAR(MAX))", "N'[1,2,3]'", "[1,2,3]", invalid: "N'[1,2]'");
    }

    private static void AssertUnsupported(TSqlDialect dialect, DatabaseObjectName identity, StructuredColumnType type)
        => Assert.That(() => new SchemaScriptRenderer(dialect).Plan(
            new([new Table(identity, [new Column("v", type)], [])])), Throws.TypeOf<NotSupportedException>());

    private static SqlConnection Connection()
        => new(Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable SQL Server QA database."));

    private static TSqlDialect Dialect(SqlConnection connection, out Version version)
    {
        version = Version.Parse(connection.ServerVersion);
        return Dialect(version);
    }

    private static TSqlDialect Dialect(Version version)
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["mssqlserver"]);
        return builder.Build().Get<TSqlDialect>().WithServerVersion(version);
    }
}
