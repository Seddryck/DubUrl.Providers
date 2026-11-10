using System.Data;
using System.Data.Common;
using DubUrl.Schema;
using DubUrl.Mapping;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using Npgsql;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.Timescale.QA.Query;

[TestFixture, Category("Timescale"), Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task TimescaleRegistrationPlansPostgreSqlDefaults()
    {
        DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);
        var registry = new ProviderCatalogBuilder().AddModule(new AttributeProviderModule([typeof(PgsqlDialect).Assembly])).Build();
        var url = new ConnectionUrl(Environment.GetEnvironmentVariable("DUBURL_TIMESCALE_SCHEMA_QA_URL") ?? new TimescaleProviderTests().SchemaQaUrl, registry);
        using var connection = (NpgsqlConnection)url.Connect();
        await connection.OpenAsync();
        Assert.That(Scalar(connection, "SELECT extname FROM pg_extension WHERE extname='timescaledb'"), Is.EqualTo("timescaledb"));
        Execute(connection, "SET TIME ZONE 'Asia/Kolkata'");
        var dialect = ((PgsqlDialect)url.Dialect).WithServerVersion(connection.PostgreSqlVersion);
        await TemporalDefaultChecks.VerifyAsync(connection, dialect, new PostgreSqlMetadataReader(),
            new("typed_temporal_defaults", "public"), TimeSpan.FromMinutes(330));

        var identity = new DatabaseObjectName("typed_timescale_defaults", "public");
        var sequence = new DatabaseObjectName("typed_timescale_seq", "public");
        Execute(connection, "CREATE SEQUENCE public.typed_timescale_seq START 19");
        var columns = new Column[]
        {
            new VarLengthColumn("login_name", DbType.AnsiString, 63, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("database_name", DbType.AnsiString, 63, defaultExpression: ColumnDefaultExpression.CurrentDatabase),
            new("uuid_value", DbType.Guid, defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Native)),
            new("sequence_value", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(sequence))
        };
        var table = Create(connection, dialect, identity, columns);
        try
        {
            var discovered = await DiscoverAsync(connection, new PostgreSqlMetadataReader(), identity);
            Assert.That(discovered.Columns.Values.Select(column => column.DefaultExpression),
                Is.EqualTo(columns.Select(column => column.DefaultExpression)));
            Execute(connection, $"INSERT INTO {table} DEFAULT VALUES");
            Assert.That(Scalar(connection, $"SELECT login_name FROM {table}"), Is.EqualTo(Scalar(connection, "SELECT CURRENT_USER")));
            Assert.That(Scalar(connection, $"SELECT database_name FROM {table}"), Is.EqualTo(connection.Database));
            Assert.That(Scalar(connection, $"SELECT uuid_value FROM {table}"), Is.TypeOf<Guid>());
            Assert.That(Scalar(connection, $"SELECT sequence_value FROM {table}"), Is.EqualTo(19));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, "DROP SEQUENCE public.typed_timescale_seq");
        }
    }
}
