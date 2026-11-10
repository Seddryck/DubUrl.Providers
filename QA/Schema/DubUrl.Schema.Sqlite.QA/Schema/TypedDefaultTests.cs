using System.Data;
using DubUrl.Provider.Sqlite.Discovery;
using DubUrl.Provider.Sqlite.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace DubUrl.Schema.Sqlite.QA;

[TestFixture]
[Category("Sqlite")]
[Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task UuidTextPreservesVersionVariantAndExplicitInput()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var identity = new DatabaseObjectName("typed_uuid", database: "main");
        var expression = ColumnDefaultExpression.NewUuid(UuidRepresentation.CanonicalText);
        var table = TypedDefaultChecks.Create(connection, Dialect(), identity, new Column("id", DbType.Int32),
            new VarLengthColumn("value", DbType.String, 36, constraints: [new NullableConstraint()], defaultExpression: expression));
        TypedDefaultChecks.Execute(connection, $"INSERT INTO {table} (id) VALUES (1), (2), (3), (4), (5)");
        TypedDefaultChecks.Execute(connection, $"INSERT INTO {table} VALUES (6, '00000000-0000-0000-0000-000000000000'), (7, NULL)");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT value FROM {table} ORDER BY id";
            using var rows = command.ExecuteReader();
            var seen = new HashSet<Guid>();
            for (var i = 0; i < 5; i++)
            {
                Assert.That(rows.Read(), Is.True);
                var value = rows.GetString(0);
                Assert.That(Guid.TryParseExact(value, "D", out var uuid), Is.True);
                Assert.That(value[14], Is.EqualTo('4'));
                Assert.That("89ab", Does.Contain(value[19].ToString()));
                Assert.That(seen.Add(uuid), Is.True);
            }
            Assert.That(rows.Read(), Is.True);
            Assert.That(rows.GetString(0), Is.EqualTo(Guid.Empty.ToString("D")));
            Assert.That(rows.Read(), Is.True);
            Assert.That(rows.IsDBNull(0), Is.True);
        }

        var discovered = await TypedDefaultChecks.DiscoverAsync(connection, new SqliteMetadataReader(), identity);
        Assert.That(discovered.Columns["value"].DefaultExpression, Is.EqualTo(expression));
    }

    [Test]
    public void RejectsUnqualifiedTemporalPrecisionAndIdentityDefaults()
    {
        var columns = new Column[]
        {
            new("stamp", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentTimestamp(3)),
            new VarLengthColumn("owner", DbType.String, 128, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new("uuid", DbType.Guid, defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Native))
        };
        foreach (var column in columns)
            Assert.That(() => new SchemaScriptRenderer(Dialect()).Plan(new([new Table("unsupported", [column], [])])),
                Throws.TypeOf<ColumnDefaultPlanningException>().With.Property("Code").EqualTo(ColumnDefaultPlanningException.Unsupported));
    }

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SqliteDialect>(["sqlite"]);
        return builder.Build().Get<SqliteDialect>();
    }
}
