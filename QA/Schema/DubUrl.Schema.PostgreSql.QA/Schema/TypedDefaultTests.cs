using System.Data;
using Npgsql;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.QA;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.PostgreSql.QA;

[TestFixture]
[Category("PostgreSql")]
[Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task EffectiveUserChangesWhileSessionIdentityAndUuidArePreserved()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var role = "typed_role_" + Guid.NewGuid().ToString("N")[..8];
        var identity = new DatabaseObjectName("typed_context", "public");
        var renderer = Dialect(connection.PostgreSqlVersion);
        var expected = new[]
        {
            ColumnDefaultExpression.CurrentUser,
            ColumnDefaultExpression.SessionUser,
            ColumnDefaultExpression.CurrentSchema,
            ColumnDefaultExpression.CurrentDatabase,
            ColumnDefaultExpression.NewUuid(UuidRepresentation.Native)
        };
        var table = Create(connection, renderer, identity,
            new VarLengthColumn("effective_user", DbType.AnsiString, 128, defaultExpression: expected[0]),
            new VarLengthColumn("session_identity", DbType.AnsiString, 128, defaultExpression: expected[1]),
            new VarLengthColumn("current_schema_name", DbType.AnsiString, 128, constraints: [new NullableConstraint()], defaultExpression: expected[2]),
            new VarLengthColumn("current_database_name", DbType.AnsiString, 128, defaultExpression: expected[3]),
            new Column("uuid_value", DbType.Guid, defaultExpression: expected[4]));
        Execute(connection, $"CREATE ROLE {role}");
        try
        {
            Execute(connection, $"GRANT INSERT ON TABLE {table} TO {role}");
            var sessionUser = Scalar(connection, "SELECT SESSION_USER");
            Execute(connection, $"SET ROLE {role}");
            try
            {
                Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
                Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
            }
            finally
            {
                Execute(connection, "RESET ROLE");
            }
            Assert.That(Scalar(connection, $"SELECT DISTINCT effective_user FROM {table}"), Is.EqualTo(role));
            Assert.That(Scalar(connection, $"SELECT DISTINCT session_identity FROM {table}"), Is.EqualTo(sessionUser));
            Assert.That(Scalar(connection, $"SELECT COUNT(DISTINCT uuid_value) FROM {table}"), Is.EqualTo(2));
            var result = await DiscoverAsync(connection, new PostgreSqlMetadataReader(), identity);
            Assert.That(result.Columns.Values.Select(column => column.DefaultExpression), Is.EqualTo(expected));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, $"DROP ROLE {role}");
        }
    }

    [Test]
    public async Task SequenceDiscoveryDoesNotAdvanceOrTakeOwnership()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var name = new DatabaseObjectName("dot. apostrophe' and \" quote", "public");
        var identity = new DatabaseObjectName("typed_sequence_one", "public");
        var second = new DatabaseObjectName("typed_sequence_two", "public");
        Execute(connection, """CREATE SEQUENCE public."dot. apostrophe' and "" quote" START 5 INCREMENT 3""");
        var column = new Column("number", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(name));
        var dialect = Dialect(connection.PostgreSqlVersion);
        var table = Create(connection, dialect, identity, column);
        var other = Create(connection, dialect, second, column);
        try
        {
            var result = await DiscoverAsync(connection, new PostgreSqlMetadataReader(), identity);
            Assert.That(result.Columns["number"].Generation, Is.Null);
            Assert.That(result.Columns["number"].DefaultExpression?.Sequence, Is.EqualTo(name));
            Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
            Execute(connection, $"INSERT INTO {other} DEFAULT VALUES");
            Execute(connection, $"INSERT INTO {table} VALUES (99)");
            Assert.That(Scalar(connection, $"SELECT MIN(number) FROM {table}"), Is.EqualTo(5));
            Assert.That(Scalar(connection, $"SELECT number FROM {other}"), Is.EqualTo(8));
            Assert.That(Scalar(connection, $"SELECT MAX(number) FROM {table}"), Is.EqualTo(99));

            using (var transaction = connection.BeginTransaction())
            {
                Execute(connection, $"INSERT INTO {other} DEFAULT VALUES");
                transaction.Rollback();
            }
            Execute(connection, $"INSERT INTO {other} DEFAULT VALUES");
            Assert.That(Scalar(connection, $"SELECT MAX(number) FROM {other}"), Is.EqualTo(14));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {other}");
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, "DROP SEQUENCE public.\"dot. apostrophe' and \"\" quote\"");
        }
    }

    [Test]
    public async Task TemporalPrecisionIsPreservedInStorageAndDiscovery()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var identity = new DatabaseObjectName("typed_precision", "public");
        var expression = ColumnDefaultExpression.CurrentUtcTimestamp(3);
        var table = Create(connection, Dialect(connection.PostgreSqlVersion), identity,
            new Column("stamp", DbType.DateTime, defaultExpression: expression));
        try
        {
            Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
            var stamp = (DateTime)Scalar(connection, $"SELECT stamp FROM {table}")!;
            Assert.That(stamp.Ticks % TimeSpan.TicksPerMillisecond, Is.Zero);
            var result = await DiscoverAsync(connection, new PostgreSqlMetadataReader(), identity);
            Assert.That(result.Columns["stamp"].DefaultExpression, Is.EqualTo(expression));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }
    }

    [Test]
    public async Task MissingSequenceAndMissingUsagePrivilegeReportNativeErrors()
    {
        using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var role = "typed_seq_role_" + Guid.NewGuid().ToString("N")[..8];
        var identity = new DatabaseObjectName("typed_sequence_permission", "public");
        var sequence = new DatabaseObjectName("typed_seq_permission", "public");
        var column = new Column("value", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(sequence));
        var dialect = Dialect(connection.PostgreSqlVersion);
        Assert.That(() => Create(connection, dialect, identity, column),
            Throws.TypeOf<PostgresException>().With.Property("SqlState").EqualTo(PostgresErrorCodes.UndefinedTable));

        Execute(connection, "CREATE SEQUENCE public.typed_seq_permission");
        var table = Create(connection, dialect, identity, column);
        Execute(connection, $"CREATE ROLE {role}");
        try
        {
            Execute(connection, $"GRANT INSERT ON TABLE {table} TO {role}");
            Execute(connection, $"SET ROLE {role}");
            try
            {
                Assert.That(() => Execute(connection, $"INSERT INTO {table} DEFAULT VALUES"),
                    Throws.TypeOf<PostgresException>().With.Property("SqlState").EqualTo(PostgresErrorCodes.InsufficientPrivilege));
            }
            finally
            {
                Execute(connection, "RESET ROLE");
            }
            Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
            Assert.That(Scalar(connection, $"SELECT value FROM {table}"), Is.EqualTo(1));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, "DROP SEQUENCE public.typed_seq_permission");
            Execute(connection, $"DROP ROLE {role}");
        }
    }

    private static IDialect Dialect(Version version)
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<PgsqlDialect>(["postgresql"]);
        return builder.Build().Get<PgsqlDialect>().WithServerVersion(version);
    }
}
