using System.Data;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using Npgsql;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.CrateDb.QA.Query;

[TestFixture, Category("CrateDb"), Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task UtcClockAndContextDefaultsUseCrateRepresentations()
    {
        var port = Environment.GetEnvironmentVariable("DUBURL_CRATE_SCHEMA_QA_PORT") ?? "5432";
        using var connection = new NpgsqlConnection($"Host=localhost;Port={port};Username=crate;Database=crate;SSL Mode=Disable");
        await connection.OpenAsync();
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<CrateDbDialect>(["crate"]);
        var dialect = builder.Build().Get<CrateDbDialect>();
        var identity = new DatabaseObjectName("typed_defaults", "doc");
        var columns = new Column[]
        {
            new("id", DbType.Int32),
            new("stamp", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentTimestamp()),
            new("utc_stamp", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp()),
            new("day", DbType.Date, nativeType: new("TIMESTAMP WITHOUT TIME ZONE", typeof(CrateDbDialect)),
                defaultExpression: ColumnDefaultExpression.CurrentDate),
            new("clock", DbType.Time, nativeType: new("BIGINT", typeof(CrateDbDialect)),
                defaultExpression: ColumnDefaultExpression.CurrentTime(3)),
            new VarLengthColumn("effective_user", DbType.String, 64, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("session_identity", DbType.String, 64, defaultExpression: ColumnDefaultExpression.SessionUser),
            new VarLengthColumn("schema_name", DbType.String, 64, defaultExpression: ColumnDefaultExpression.CurrentSchema),
            new VarLengthColumn("database_name", DbType.String, 64, defaultExpression: ColumnDefaultExpression.CurrentDatabase)
        };
        var table = Create(connection, dialect, identity, columns);
        try
        {
            Execute(connection, $"INSERT INTO {table} (id) VALUES (1)");
            Execute(connection, $"REFRESH TABLE {table}");
            var stamp = (DateTime)Scalar(connection, $"SELECT stamp FROM {table}")!;
            var utc = (DateTime)Scalar(connection, $"SELECT utc_stamp FROM {table}")!;
            Assert.That(stamp, Is.EqualTo(utc));
            Assert.That(stamp, Is.InRange(DateTime.UtcNow.AddSeconds(-10), DateTime.UtcNow.AddSeconds(1)));
            Assert.That(Scalar(connection, $"SELECT day FROM {table}"), Is.EqualTo(utc.Date));
            Assert.That(Scalar(connection, $"SELECT clock FROM {table}"), Is.EqualTo(utc.TimeOfDay.Ticks / 10));
            Assert.That(Scalar(connection, $"SELECT effective_user FROM {table}"), Is.EqualTo("crate"));
            Assert.That(Scalar(connection, $"SELECT session_identity FROM {table}"), Is.EqualTo("crate"));
            Assert.That(Scalar(connection, $"SELECT schema_name FROM {table}"), Is.EqualTo(Scalar(connection, "SELECT CURRENT_SCHEMA")));
            Assert.That(Scalar(connection, $"SELECT database_name FROM {table}"), Is.EqualTo("crate"));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }
    }
}
