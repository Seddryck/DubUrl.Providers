using System.Data;
using System.Data.Common;
using DubUrl.Mapping;
using DubUrl.Provider.Db2.Sql;
using DubUrl.Schema;
using IBM.Data.Db2;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.Db2.QA.Query;

[TestFixture, Category("Db2"), Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public void LuwSpecialRegistersAreEvaluatedOnInsert()
    {
        DbProviderFactories.RegisterFactory("IBM.Data.Db2", DB2Factory.Instance);
        var registry = new ProviderCatalogBuilder().AddModule(new AttributeProviderModule([typeof(Db2Dialect).Assembly])).Build();
        var url = new ConnectionUrl(Db2ProviderTests.Url, registry);
        using var connection = (DbConnection)url.Open();
        var serviceLevel = (string)Scalar(connection, "SELECT SERVICE_LEVEL FROM TABLE(SYSPROC.ENV_GET_INST_INFO())")!;
        var version = Version.Parse(serviceLevel.Split('v')[^1].Trim());
        var dialect = ((Db2Dialect)url.Dialect).WithLuwColumnDefaults(version);
        var identity = new DatabaseObjectName("TYPED_DEFAULTS");
        var table = Create(connection, dialect, identity,
            new Column("ID", DbType.Int32),
            new Column("LOCAL_STAMP", DbType.DateTime, defaultExpression: ColumnDefaultExpression.CurrentTimestamp()),
            new Column("LOCAL_DAY", DbType.Date, defaultExpression: ColumnDefaultExpression.CurrentDate),
            new Column("LOCAL_CLOCK", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime()),
            new VarLengthColumn("EFFECTIVE_USER", DbType.String, 128, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("SESSION_IDENTITY", DbType.String, 128, defaultExpression: ColumnDefaultExpression.SessionUser),
            new VarLengthColumn("CURRENT_SCHEMA_NAME", DbType.String, 128, defaultExpression: ColumnDefaultExpression.CurrentSchema));
        try
        {
            Execute(connection, $"INSERT INTO {table} (ID) VALUES (1)");
            var stamp = (DateTime)Scalar(connection, $"SELECT LOCAL_STAMP FROM {table}")!;
            Assert.That(Scalar(connection, $"SELECT LOCAL_DAY FROM {table}"), Is.EqualTo(stamp.Date));
            var elapsed = Scalar(connection, $"SELECT ABS(MIDNIGHT_SECONDS(LOCAL_CLOCK) - MIDNIGHT_SECONDS(LOCAL_STAMP)) FROM {table}");
            Assert.That(Convert.ToInt32(elapsed), Is.LessThanOrEqualTo(1));
            Assert.That(Scalar(connection, $"SELECT EFFECTIVE_USER FROM {table}"), Is.EqualTo(Scalar(connection, "VALUES CURRENT USER")));
            Assert.That(Scalar(connection, $"SELECT SESSION_IDENTITY FROM {table}"), Is.EqualTo(Scalar(connection, "VALUES SESSION_USER")));
            Assert.That(Scalar(connection, $"SELECT CURRENT_SCHEMA_NAME FROM {table}"), Is.EqualTo(Scalar(connection, "VALUES CURRENT SCHEMA")));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }
    }
}
