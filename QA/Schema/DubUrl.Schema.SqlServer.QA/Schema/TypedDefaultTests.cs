using System.Data;
using Microsoft.Data.SqlClient;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.SqlServer.QA;

[TestFixture]
[Category("SqlServer")]
[Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task EffectiveUserChangesWhileSessionIdentityAndUuidArePreserved()
    {
        using var connection = new SqlConnection(Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable SQL Server QA database."));
        await connection.OpenAsync();
        var role = "typed_role_" + Guid.NewGuid().ToString("N")[..8];
        var identity = new DatabaseObjectName("typed_context", "dbo");
        var renderer = Dialect(Version.Parse(connection.ServerVersion));
        var expected = new[]
        {
            ColumnDefaultExpression.CurrentUser,
            ColumnDefaultExpression.SessionUser,
            ColumnDefaultExpression.CurrentSchema,
            ColumnDefaultExpression.CurrentDatabase,
            ColumnDefaultExpression.NewUuid(UuidRepresentation.Native)
        };
        var table = Create(connection, renderer, identity,
            new VarLengthColumn("effective_user", DbType.String, 128, defaultExpression: expected[0]),
            new VarLengthColumn("session_identity", DbType.String, 128, defaultExpression: expected[1]),
            new VarLengthColumn("current_schema_name", DbType.String, 128, constraints: [new NullableConstraint()], defaultExpression: expected[2]),
            new VarLengthColumn("current_database_name", DbType.String, 128, defaultExpression: expected[3]),
            new Column("uuid_value", DbType.Guid, defaultExpression: expected[4]));
        Execute(connection, $"CREATE USER [{role}] WITHOUT LOGIN");
        try
        {
            Execute(connection, $"GRANT INSERT ON {table} TO {role}");
            var sessionUser = Scalar(connection, "SELECT ORIGINAL_LOGIN()");
            Execute(connection, $"EXECUTE AS USER = '{role}'");
            try
            {
                Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
                Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
            }
            finally
            {
                Execute(connection, "REVERT");
            }
            Assert.That(Scalar(connection, $"SELECT DISTINCT effective_user FROM {table}"), Is.EqualTo(role));
            Assert.That(Scalar(connection, $"SELECT DISTINCT session_identity FROM {table}"), Is.EqualTo(sessionUser));
            Assert.That(Scalar(connection, $"SELECT COUNT(DISTINCT uuid_value) FROM {table}"), Is.EqualTo(2));
            var result = await DiscoverAsync(connection, new SqlServerMetadataReader(), identity);
            Assert.That(result.Columns.Values.Select(column => column.DefaultExpression), Is.EqualTo(expected));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, $"DROP USER [{role}]");
        }
    }

    [Test]
    public async Task SequenceDiscoveryDoesNotAdvanceOrTakeOwnership()
    {
        using var connection = new SqlConnection(Environment.GetEnvironmentVariable("DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable SQL Server QA database."));
        await connection.OpenAsync();
        var name = new DatabaseObjectName("dot. apostrophe' and ] bracket", "dbo");
        var identity = new DatabaseObjectName("typed_sequence_one", "dbo");
        var second = new DatabaseObjectName("typed_sequence_two", "dbo");
        Execute(connection, """CREATE SEQUENCE dbo.[dot. apostrophe' and ]] bracket] START WITH 5 INCREMENT BY 3""");
        var column = new Column("number", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(name));
        var dialect = Dialect(Version.Parse(connection.ServerVersion));
        var table = Create(connection, dialect, identity, column);
        var other = Create(connection, dialect, second, column);
        try
        {
            var result = await DiscoverAsync(connection, new SqlServerMetadataReader(), identity);
            Assert.That(result.Columns["number"].Generation, Is.Null);
            Assert.That(result.Columns["number"].DefaultExpression?.Sequence, Is.EqualTo(name));
            Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
            Execute(connection, $"INSERT INTO {other} DEFAULT VALUES");
            Execute(connection, $"INSERT INTO {table} VALUES (99)");
            Assert.That(Scalar(connection, $"SELECT MIN(number) FROM {table}"), Is.EqualTo(5));
            Assert.That(Scalar(connection, $"SELECT number FROM {other}"), Is.EqualTo(8));
            Assert.That(Scalar(connection, $"SELECT MAX(number) FROM {table}"), Is.EqualTo(99));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {other}");
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, """DROP SEQUENCE dbo.[dot. apostrophe' and ]] bracket]""");
        }
    }

    private static IDialect Dialect(Version version)
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TSqlDialect>(["sqlserver"]);
        return builder.Build().Get<TSqlDialect>().WithServerVersion(version);
    }
}
