using System.Data;
using DubUrl.Provider.Oracle.Discovery;
using DubUrl.Provider.Oracle.QA;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.Oracle.QA;

[TestFixture]
[Category("Oracle")]
[Category("Schema")]
public sealed class TypedDefaultTests
{
    [Test]
    public async Task DefaultsAreEvaluatedOnInsertAndRecognizedInTheCatalog()
    {
        using var connection = OracleTestDatabase.Open();
        Execute(connection, "ALTER SESSION SET TIME_ZONE = '+05:30'");
        var owner = (string)Scalar(connection, "SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM dual")!;
        var version = new Version(int.Parse(connection.ServerVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture), 0);
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<OracleDialect>(["oracle"]);
        var dialect = builder.Build().Get<OracleDialect>().WithServerVersion(version);
        var identity = new DatabaseObjectName("TYPED_DEFAULTS", owner);
        var sequence = new DatabaseObjectName("Sequence.dot ' " + Guid.NewGuid().ToString("N")[..8], owner);
        var sequenceSql = $"\"{owner}\".\"{sequence.Name}\"";
        Execute(connection, $"CREATE SEQUENCE {sequenceSql} START WITH 17 INCREMENT BY 3");

        var columns = new Column[]
        {
            new("ID", DbType.Int32),
            new("LOCAL_STAMP", DbType.DateTime, constraints: [new NullableConstraint()], defaultExpression: ColumnDefaultExpression.CurrentTimestamp()),
            new("LOCAL_DATE", DbType.Date, constraints: [new NullableConstraint()], defaultExpression: ColumnDefaultExpression.CurrentDate),
            new("UTC_STAMP", DbType.DateTime, constraints: [new NullableConstraint()], defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp()),
            new VarLengthColumn("EFFECTIVE_USER", DbType.String, 128, defaultExpression: ColumnDefaultExpression.CurrentUser),
            new VarLengthColumn("SESSION_IDENTITY", DbType.String, 128, defaultExpression: ColumnDefaultExpression.SessionUser),
            new VarLengthColumn("CURRENT_SCHEMA_NAME", DbType.String, 128, defaultExpression: ColumnDefaultExpression.CurrentSchema),
            new VarLengthColumn("DATABASE_NAME", DbType.String, 8, defaultExpression: ColumnDefaultExpression.CurrentDatabase),
            new VarLengthColumn("GUID_VALUE", DbType.Binary, 16, nativeType: new("RAW", typeof(OracleDialect)),
                defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Binary, UuidByteOrder.Network)),
            new("SEQUENCE_VALUE", DbType.Int64, nativeType: new("NUMBER", typeof(OracleDialect)),
                defaultExpression: ColumnDefaultExpression.NextSequenceValue(sequence))
        };
        var table = Create(connection, dialect, identity, columns);
        try
        {
            Execute(connection, $"INSERT INTO {table} (ID) VALUES (1)");
            Execute(connection, "ALTER SESSION SET CURRENT_SCHEMA = SYS");
            try
            {
                Execute(connection, $"INSERT INTO {table} (ID, LOCAL_STAMP, LOCAL_DATE, UTC_STAMP) VALUES (2, NULL, NULL, NULL)");
            }
            finally
            {
                Execute(connection, $"ALTER SESSION SET CURRENT_SCHEMA = \"{owner.Replace("\"", "\"\"")}\"");
            }
            var local = (DateTime)Scalar(connection, $"SELECT LOCAL_STAMP FROM {table} WHERE ID=1")!;
            var utc = (DateTime)Scalar(connection, $"SELECT UTC_STAMP FROM {table} WHERE ID=1")!;
            Assert.That(local - utc, Is.EqualTo(TimeSpan.FromMinutes(330)).Within(TimeSpan.FromSeconds(1)));
            Assert.That(Scalar(connection, $"SELECT LOCAL_DATE FROM {table} WHERE ID=1"), Is.EqualTo(local.Date));
            Assert.That(Scalar(connection, $"SELECT LOCAL_STAMP FROM {table} WHERE ID=2"), Is.EqualTo(DBNull.Value));
            Assert.That(Scalar(connection, $"SELECT EFFECTIVE_USER FROM {table} WHERE ID=1"), Is.EqualTo(owner));
            Assert.That(Scalar(connection, $"SELECT SESSION_IDENTITY FROM {table} WHERE ID=1"), Is.EqualTo(owner));
            Assert.That(Scalar(connection, $"SELECT CURRENT_SCHEMA_NAME FROM {table} WHERE ID=1"), Is.EqualTo(owner));
            Assert.That(Scalar(connection, $"SELECT CURRENT_SCHEMA_NAME FROM {table} WHERE ID=2"), Is.EqualTo("SYS"));
            Assert.That(Scalar(connection, $"SELECT EFFECTIVE_USER FROM {table} WHERE ID=2"), Is.EqualTo(owner));
            Assert.That(Scalar(connection, $"SELECT DATABASE_NAME FROM {table} WHERE ID=1"),
                Is.EqualTo(Scalar(connection, "SELECT SYS_CONTEXT('USERENV','DB_NAME') FROM dual")));
            Assert.That(Scalar(connection, $"SELECT GUID_VALUE FROM {table} WHERE ID=1"), Is.TypeOf<byte[]>().And.Length.EqualTo(16));
            Assert.That(Scalar(connection, $"SELECT GUID_VALUE FROM {table} WHERE ID=2"), Is.Not.EqualTo(
                Scalar(connection, $"SELECT GUID_VALUE FROM {table} WHERE ID=1")));
            Assert.That(Scalar(connection, $"SELECT SEQUENCE_VALUE FROM {table} WHERE ID=1"), Is.EqualTo(17));

            var discovered = await DiscoverAsync(connection, new OracleMetadataReader(version), identity, inspectGeneration: false);
            foreach (var column in columns.Where(column => column.DefaultExpression is not null))
                Assert.That(discovered.Columns[column.Name].DefaultExpression, Is.EqualTo(column.DefaultExpression), column.Name);
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
            Execute(connection, $"DROP SEQUENCE {sequenceSql}");
        }
    }
}
