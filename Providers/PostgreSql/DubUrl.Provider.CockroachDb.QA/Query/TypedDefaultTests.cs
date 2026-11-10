using System.Data;
using System.Data.Common;
using DubUrl.Mapping;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using Npgsql;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.CockroachDb.QA.Query;

[TestFixture, Category("CockroachDB"), Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task DistinctSessionIdentityAndUtcClockEvaluateOnInsert()
    {
        DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);
        var registry = new ProviderCatalogBuilder().AddModule(new AttributeProviderModule([typeof(CockRoachDialect).Assembly])).Build();
        var url = new ConnectionUrl("cr://root@localhost/duburl?sslmode=disable&Timeout=5", registry);
        using var connection = (NpgsqlConnection)url.Connect();
        await connection.OpenAsync();
        Execute(connection, "SET TIME ZONE 'Asia/Kolkata'");
        var role = "typed_role_" + Guid.NewGuid().ToString("N")[..8];
        var identity = new DatabaseObjectName("typed_defaults", "public");
        var sequence = new DatabaseObjectName("seq.typed", "public");
        var sequenceSql = "public.\"seq.typed\"";
        Execute(connection, $"CREATE SEQUENCE {sequenceSql} START 17 INCREMENT 3");
        var columns = new Column[]
        {
            new("local_stamp", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentTimestamp()),
            new("local_date", DbType.Date, defaultExpression: ColumnDefaultExpression.CurrentDate),
            new("local_time", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime()),
            new("utc_stamp", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp()),
            new VarLengthColumn("effective_user", DbType.AnsiString, 63, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("session_identity", DbType.AnsiString, 63, defaultExpression: ColumnDefaultExpression.SessionUser),
            new VarLengthColumn("schema_name", DbType.AnsiString, 128, constraints: [new NullableConstraint()],
                defaultExpression: ColumnDefaultExpression.CurrentSchema),
            new VarLengthColumn("database_name", DbType.AnsiString, 128, constraints: [new NullableConstraint()],
                defaultExpression: ColumnDefaultExpression.CurrentDatabase),
            new("uuid_value", DbType.Guid, defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Native)),
            new("sequence_value", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(sequence))
        };
        var table = Create(connection, (CockRoachDialect)url.Dialect, identity, columns);
        Execute(connection, $"CREATE ROLE {role}");
        try
        {
            Execute(connection, $"GRANT INSERT ON TABLE {table} TO {role}");
            Execute(connection, $"GRANT USAGE ON SEQUENCE {sequenceSql} TO {role}");
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
            Assert.That(Scalar(connection, $"SELECT DISTINCT session_identity FROM {table}"), Is.EqualTo("root"));
            Assert.That(Scalar(connection, $"SELECT DISTINCT schema_name FROM {table}"), Is.EqualTo("public"));
            Assert.That(Scalar(connection, $"SELECT DISTINCT database_name FROM {table}"), Is.EqualTo(connection.Database));
            Assert.That(Scalar(connection, $"SELECT COUNT(DISTINCT uuid_value) FROM {table}"), Is.EqualTo(2));
            Assert.That(Scalar(connection, $"SELECT MIN(sequence_value) FROM {table}"), Is.EqualTo(17));
            Assert.That(Scalar(connection, $"SELECT MAX(sequence_value) FROM {table}"), Is.EqualTo(20));
            var local = (DateTime)Scalar(connection, $"SELECT local_stamp FROM {table} LIMIT 1")!;
            var utc = (DateTime)Scalar(connection, $"SELECT utc_stamp FROM {table} LIMIT 1")!;
            Assert.That(local - utc, Is.EqualTo(TimeSpan.FromMinutes(330)).Within(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, $"DROP SEQUENCE {sequenceSql}");
            Execute(connection, $"DROP ROLE {role}");
        }
    }
}
