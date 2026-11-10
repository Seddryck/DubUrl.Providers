using System.Data;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.SingleStore.QA;

[TestFixture]
[Category("SingleStore")]
[Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task TimestampDefaultEvaluatesOnInsertAndIsDiscovered()
    {
        using var connection = SingleStoreTestDatabase.CreateConnection();
        await connection.OpenAsync();
        var dialect = Dialect(Version.Parse(((string)Scalar(connection, "SELECT @@memsql_version")!).Split('-')[0]));
        var identity = new DatabaseObjectName("typed_timestamp", database: connection.Database);
        var expression = ColumnDefaultExpression.CurrentTimestamp();
        var table = Create(connection, dialect, identity, new Column("id", DbType.Int32),
            new Column("stamp", DbType.DateTime, defaultExpression: expression));
        try
        {
            Execute(connection, $"INSERT INTO {table} (id) VALUES (1)");
            Assert.That(Scalar(connection, $"SELECT stamp FROM {table} WHERE id=1"), Is.TypeOf<DateTime>());
            var discovered = await DiscoverAsync(connection, new SingleStoreMetadataReader(), identity);
            Assert.That(discovered.Columns["stamp"].DefaultExpression, Is.EqualTo(expression));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }
    }

    [Test]
    public void OrdinaryTablesRejectUnsupportedTemporalAndUuidDefaultsBeforeDdl()
    {
        var defaults = new Column[]
        {
            new("value", DbType.Date, defaultExpression: ColumnDefaultExpression.CurrentDate),
            new("value", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime()),
            new("value", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp()),
            new VarLengthColumn("value", DbType.AnsiString, 36,
                defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.CanonicalText))
        };
        var renderer = new SchemaScriptRenderer(Dialect(new Version(9, 1, 1)));
        foreach (var column in defaults)
            Assert.That(() => renderer.Plan(new([new Table("typed_unsupported", [column], [])])),
                Throws.TypeOf<ColumnDefaultPlanningException>());
    }

    private static SingleStoreDialect Dialect(Version version)
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SingleStoreDialect>(["singlestore"]);
        return builder.Build().Get<SingleStoreDialect>().WithServerVersion(version);
    }
}
