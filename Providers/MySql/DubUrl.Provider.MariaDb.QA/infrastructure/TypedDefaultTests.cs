using System.Data;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using MySqlConnector;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.MariaDb.QA.infrastructure;

[TestFixture, Category("MariaDb"), Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task MariaDbDefaultsAreQualifiedIndependently()
    {
        System.Data.Common.DbProviderFactories.RegisterFactory("MySqlConnector", MySqlConnectorFactory.Instance);
        using var connection = (MySqlConnection)new ConnectionUrl(MariaDbTestDatabase.ConnectionUrl).Connect();
        await connection.OpenAsync();
        Execute(connection, "SET time_zone = '+05:30'");
        var version = Version.Parse(connection.ServerVersion.Split('-')[0]);
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MySqlDialect>(["mariadb"]);
        var dialect = builder.Build().Get<MySqlDialect>().WithMariaDbDefaults(version);
        var reader = new MariaDbMetadataReader();
        await TemporalDefaultChecks.VerifyAsync(connection, dialect, reader,
            new("typed_temporal_defaults", database: connection.Database), TimeSpan.FromMinutes(330), DbType.DateTime2);

        var identity = new DatabaseObjectName("typed_context", database: connection.Database);
        var sequence = new DatabaseObjectName("seq.typed", database: connection.Database);
        var sequenceSql = $"`{connection.Database}`.`seq.typed`";
        Execute(connection, $"CREATE SEQUENCE {sequenceSql} START 17 INCREMENT 3");
        var columns = new Column[]
        {
            new("id", DbType.Int32),
            new VarLengthColumn("effective_user", DbType.AnsiString, 288, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("session_identity", DbType.AnsiString, 288, defaultExpression: ColumnDefaultExpression.SessionUser),
            new VarLengthColumn("database_name", DbType.AnsiString, 64, constraints: [new NullableConstraint()],
                defaultExpression: ColumnDefaultExpression.CurrentDatabase),
            new VarLengthColumn("uuid_value", DbType.AnsiString, 36,
                defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.CanonicalText)),
            new("sequence_value", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(sequence)),
            new VarLengthColumn("literal_expression", DbType.AnsiString, 36, defaultValue: "CURRENT_USER()")
        };
        var table = Create(connection, dialect, identity, columns);
        try
        {
            var discovered = await DiscoverAsync(connection, reader, identity);
            foreach (var column in columns.Where(column => column.DefaultExpression is not null))
                Assert.That(discovered.Columns[column.Name].DefaultExpression, Is.EqualTo(column.DefaultExpression), column.Name);
            Assert.That(discovered.Columns["literal_expression"].DefaultValue, Is.EqualTo("CURRENT_USER()"));
            Execute(connection, $"INSERT INTO {table} (id) VALUES (1), (2)");
            Assert.That(Scalar(connection, $"SELECT effective_user FROM {table} WHERE id=1"), Is.EqualTo(Scalar(connection, "SELECT CURRENT_USER()")));
            Assert.That(Scalar(connection, $"SELECT session_identity FROM {table} WHERE id=1"), Is.EqualTo(Scalar(connection, "SELECT SESSION_USER()")));
            Assert.That(Scalar(connection, $"SELECT database_name FROM {table} WHERE id=1"), Is.EqualTo(connection.Database));
            Assert.That(Scalar(connection, $"SELECT COUNT(DISTINCT uuid_value) FROM {table}"), Is.EqualTo(2));
            Assert.That(Scalar(connection, $"SELECT MIN(sequence_value) FROM {table}"), Is.EqualTo(17));
            Assert.That(Scalar(connection, $"SELECT MAX(sequence_value) FROM {table}"), Is.EqualTo(20));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, $"DROP SEQUENCE {sequenceSql}");
        }
    }
}
