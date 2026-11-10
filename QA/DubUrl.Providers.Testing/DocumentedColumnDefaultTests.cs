using System.Data;
using DubUrl.Provider.Db2.Sql;
using DubUrl.Provider.Snowflake.Sql;
using DubUrl.Provider.Teradata.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Renderers;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

[TestFixture]
public sealed class DocumentedColumnDefaultTests
{
    [Test]
    public void Db2DefaultsRequireAnExplicitLuwBaseline()
    {
        var dialect = Dialect<Db2Dialect>("db2");
        var column = new Column("stamp", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentTimestamp());
        Assert.That(() => Render(dialect, column), Throws.TypeOf<ColumnDefaultPlanningException>());
        Assert.That(() => Render(dialect.WithLuwColumnDefaults(new(11, 1)), column), Throws.TypeOf<ColumnDefaultPlanningException>());

        var luw = dialect.WithLuwColumnDefaults(new(11, 5));
        Assert.That(Render(luw, column), Does.Contain("DEFAULT CURRENT TIMESTAMP"));
        Assert.That(Render(luw, new Column("clock", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime())),
            Does.Contain("DEFAULT CURRENT TIME"));
        Assert.That(Render(luw, new VarLengthColumn("schema_name", DbType.String, 128, defaultExpression: ColumnDefaultExpression.CurrentSchema)),
            Does.Contain("DEFAULT CURRENT SCHEMA"));
        Assert.That(() => Render(luw, new Column("stamp", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp())),
            Throws.TypeOf<ColumnDefaultPlanningException>());
    }

    [Test]
    public void TeradataDefaultsUseTheDocumentedKeywordForms()
    {
        var dialect = Dialect<TeradataDialect>("teradata");
        var column = new Column("clock", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime(3));
        Assert.That(() => Render(dialect, column), Throws.TypeOf<ColumnDefaultPlanningException>());
        var vantage = dialect.WithVantageColumnDefaults(new(17, 20));
        Assert.That(Render(vantage, column), Does.Contain("DEFAULT CURRENT_TIME(3)"));
        Assert.That(Render(vantage, new VarLengthColumn("effective_user", DbType.String, 128, defaultExpression: ColumnDefaultExpression.CurrentUser)),
            Does.Contain("DEFAULT USER"));
        Assert.That(() => Render(vantage, new VarLengthColumn("original_user", DbType.String, 128, defaultExpression: ColumnDefaultExpression.SessionUser)),
            Throws.TypeOf<ColumnDefaultPlanningException>());
    }

    [Test]
    public void SnowflakeUsesExplicitUtcTypeAndQuotesExternalSequences()
    {
        var dialect = Dialect<SnowflakeDialect>("snowflake");
        var timestamp = new Column("stamp", DbType.DateTime2, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp(3));
        Assert.That(() => Render(dialect, timestamp), Throws.TypeOf<ColumnDefaultPlanningException>());
        var standard = dialect.WithStandardTableDefaults();
        Assert.That(Render(standard, timestamp),
            Does.Contain("TIMESTAMP_NTZ").And.Contain("CONVERT_TIMEZONE('UTC', CURRENT_TIMESTAMP(3))"));
        var sequence = ColumnDefaultExpression.NextSequenceValue(new("a.\"b", "S", "D"));
        Assert.That(Render(standard, new Column("seq", DbType.Int64, defaultExpression: sequence)),
            Does.Contain("""DEFAULT "D"."S"."a.""b".NEXTVAL"""));
        Assert.That(() => Render(standard, new VarLengthColumn("schema_name", DbType.String, 255,
            constraints: [new NotNullableConstraint()], defaultExpression: ColumnDefaultExpression.CurrentSchema)),
            Throws.TypeOf<ColumnDefaultPlanningException>());
    }

    private static string Render(IDialect dialect, Column column)
        => new SchemaScriptRenderer(dialect).Render(new([new Table("typed_defaults", [column], [])]));

    private static T Dialect<T>(string alias) where T : IDialect
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<T>([alias]);
        return builder.Build().Get<T>();
    }
}
