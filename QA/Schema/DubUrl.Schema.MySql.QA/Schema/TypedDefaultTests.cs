using System.Data;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;
using MySqlConnector;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.MySql.QA;

[TestFixture]
[Category("MySql")]
[Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task ContextUsesInsertSessionAndUuidsUseExplicitRepresentation()
    {
        var connectionString = Environment.GetEnvironmentVariable("DUBURL_SCHEMA_MYSQL")
            ?? throw new InvalidOperationException("Configure a disposable MySQL QA database.");
        using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var dialect = Dialect().WithInnoDbDefaults(Version.Parse(connection.ServerVersion.Split('-')[0]));
        var identity = new DatabaseObjectName("typed_context", database: connection.Database);
        var table = Create(connection, dialect, identity,
            new Column("id", DbType.Int32),
            new VarLengthColumn("effective_user", DbType.AnsiString, 288, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("session_identity", DbType.AnsiString, 288, defaultExpression: ColumnDefaultExpression.SessionUser),
            new VarLengthColumn("schema_name", DbType.AnsiString, 64, constraints: [new NullableConstraint()],
                defaultExpression: ColumnDefaultExpression.CurrentSchema),
            new VarLengthColumn("database_name", DbType.AnsiString, 64, constraints: [new NullableConstraint()],
                defaultExpression: ColumnDefaultExpression.CurrentDatabase),
            new VarLengthColumn("uuid_text", DbType.AnsiString, 36,
                defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.CanonicalText)),
            new VarLengthColumn("uuid_binary", DbType.Binary, 16,
                defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Binary, UuidByteOrder.Network)));
        try
        {
            Execute(connection, $"INSERT INTO {table} (id) VALUES (1), (2)");
            using var unscoped = new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString);
            await unscoped.OpenAsync();
            Execute(unscoped, $"INSERT INTO {table} (id) VALUES (3)");
            Assert.That(Scalar(connection, $"SELECT effective_user FROM {table} WHERE id=1"), Is.EqualTo(Scalar(connection, "SELECT CURRENT_USER()")));
            Assert.That(Scalar(connection, $"SELECT session_identity FROM {table} WHERE id=1"), Is.EqualTo(Scalar(connection, "SELECT SESSION_USER()")));
            Assert.That(Scalar(connection, $"SELECT schema_name FROM {table} WHERE id=1"), Is.EqualTo(connection.Database));
            Assert.That(Scalar(connection, $"SELECT database_name FROM {table} WHERE id=1"), Is.EqualTo(connection.Database));
            Assert.That(Scalar(connection, $"SELECT schema_name FROM {table} WHERE id=3"), Is.EqualTo(DBNull.Value));
            Assert.That(Scalar(connection, $"SELECT database_name FROM {table} WHERE id=3"), Is.EqualTo(DBNull.Value));
            Assert.That(Guid.TryParseExact((string)Scalar(connection, $"SELECT uuid_text FROM {table} WHERE id=1")!, "D", out _), Is.True);
            Assert.That(Scalar(connection, $"SELECT COUNT(DISTINCT uuid_binary) FROM {table}"), Is.EqualTo(3));
            Assert.That(Scalar(connection, $"SELECT OCTET_LENGTH(uuid_binary) FROM {table} WHERE id=1"), Is.EqualTo(16));
            var result = await DiscoverAsync(connection, new MySqlMetadataReader(), identity);
            Assert.That(result.Columns["effective_user"].DefaultExpression, Is.EqualTo(ColumnDefaultExpression.CurrentUser));
            Assert.That(result.Columns["session_identity"].DefaultExpression, Is.EqualTo(ColumnDefaultExpression.SessionUser));
            Assert.That(result.Columns["schema_name"].DefaultExpression?.Kind, Is.AnyOf(ColumnDefaultKind.CurrentSchema, ColumnDefaultKind.CurrentDatabase));
            Assert.That(result.Columns["database_name"].DefaultExpression, Is.EqualTo(ColumnDefaultExpression.CurrentDatabase));
            Assert.That(result.Columns["uuid_text"].DefaultExpression?.Representation, Is.EqualTo(UuidRepresentation.CanonicalText));
            Assert.That(result.Columns["uuid_binary"].DefaultExpression?.ByteOrder, Is.EqualTo(UuidByteOrder.Network));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }
    }

    [Test]
    public void UserDefaultRequiresMySql8034AndContextCapacity()
    {
        var column = new VarLengthColumn("login_name", DbType.AnsiString, 288, defaultExpression: ColumnDefaultExpression.CurrentUser);
        var schema = new DubUrl.Schema.Schema([new Table("typed_users", [column], [])]);
        foreach (var dialect in new[] { Dialect(), Dialect().WithInnoDbDefaults(new(8, 0, 33)) })
            Assert.That(() => new SchemaScriptRenderer(dialect).Plan(schema), Throws.TypeOf<ColumnDefaultPlanningException>());
        Assert.That(() => new SchemaScriptRenderer(Dialect().WithInnoDbDefaults(new(8, 0, 34))).Plan(schema), Throws.Nothing);
    }

    private static MySqlDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MySqlDialect>(["mysql"]);
        return builder.Build().Get<MySqlDialect>();
    }
}
