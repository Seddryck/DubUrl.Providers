using System.Data;
using System.Globalization;
using DubUrl.Provider.FirebirdSql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;
using FirebirdSql.Data.FirebirdClient;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.FirebirdSql.QA.Query;

[TestFixture, Category("FirebirdSQL"), Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public void Firebird5TemporalAndUserDefaultsAreNativeContextVariables()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_FIREBIRD_SCHEMA_QA_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Ignore("This case requires a disposable Firebird 5 database with DDL permissions.");

        using var connection = new FbConnection(connectionString);
        connection.Open();
        Execute(connection, "SET TIME ZONE 'Asia/Kolkata'");
        var version = (string)Scalar(connection, "SELECT RDB$GET_CONTEXT('SYSTEM', 'ENGINE_VERSION') FROM RDB$DATABASE")!;
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<FirebirdSqlDialect>(["firebird"]);
        var dialect = builder.Build().Get<FirebirdSqlDialect>().WithServerVersion(Version.Parse(version));
        var identity = new DatabaseObjectName("TYPED_DEFAULTS");
        var table = Create(connection, dialect, identity,
            new Column("ID", DbType.Int32),
            new Column("LOCAL_STAMP", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentTimestamp()),
            new Column("LOCAL_DAY", DbType.Date, defaultExpression: ColumnDefaultExpression.CurrentDate),
            new Column("LOCAL_CLOCK", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime()),
            new VarLengthColumn("EFFECTIVE_USER", DbType.AnsiString, 63, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("SESSION_IDENTITY", DbType.AnsiString, 63, defaultExpression: ColumnDefaultExpression.SessionUser));
        try
        {
            Execute(connection, $"INSERT INTO {table} (ID) VALUES (1)");
            var expected = (DateTime)Scalar(connection, "SELECT LOCALTIMESTAMP FROM RDB$DATABASE")!;
            var timestamp = (DateTime)Scalar(connection, $"SELECT LOCAL_STAMP FROM {table}")!;
            Assert.That(timestamp, Is.InRange(expected.AddSeconds(-2), expected.AddSeconds(1)));
            Assert.That(Scalar(connection, $"SELECT LOCAL_DAY FROM {table}"), Is.EqualTo(timestamp.Date));
            var stored = Scalar(connection, $"SELECT LOCAL_CLOCK FROM {table}");
            var time = stored switch
            {
                TimeSpan span => span,
                TimeOnly clock => clock.ToTimeSpan(),
                DateTime clock => clock.TimeOfDay,
                _ => TimeSpan.Parse(Convert.ToString(stored, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
            };
            Assert.That((time - timestamp.TimeOfDay).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)));
            var user = Scalar(connection, "SELECT CURRENT_USER FROM RDB$DATABASE");
            Assert.That(Scalar(connection, $"SELECT EFFECTIVE_USER FROM {table}"), Is.EqualTo(user));
            Assert.That(Scalar(connection, $"SELECT SESSION_IDENTITY FROM {table}"), Is.EqualTo(user));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }

        var utc = new Column("UTC_STAMP", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp());
        Assert.That(() => new SchemaScriptRenderer(dialect).Plan(new([new Table(identity, [utc], [])])),
            Throws.TypeOf<ColumnDefaultPlanningException>());
    }
}
