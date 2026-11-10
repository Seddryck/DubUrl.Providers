using System.Data;
using System.Data.Odbc;
using System.Data.OleDb;
using System.Runtime.Versioning;
using DubUrl.Provider.Access.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Renderers;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Provider.MsAccess.QA.Connection.Specific;

[TestFixture, Category("MsAccess"), NonParallelizable, Platform("Win"), SupportedOSPlatform("windows")]
public sealed class TypedDefaultTests
{
    [Test]
    public void AceOleDbEvaluatesDefaultsAndOdbcRejectsCreateTableDefaults()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<MsAccessDialect>(["accdb"]);
        var dialect = builder.Build().Get<MsAccessDialect>();
        var access = dialect.WithAceOleDbDefaults();
        var identity = new DatabaseObjectName("TypedDefaults");
        var columns = new Column[]
        {
            new("Id", DbType.Int32),
            new("LocalStamp", DbType.DateTime2, defaultExpression: ColumnDefaultExpression.CurrentTimestamp()),
            new("LocalDate", DbType.Date, defaultExpression: ColumnDefaultExpression.CurrentDate),
            new("LocalTime", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime()),
            new("GeneratedId", DbType.Guid, nativeType: new("GUID", typeof(MsAccessDialect)),
                defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Native))
        };
        var schema = new DubUrl.Schema.Schema([new Table(identity, columns, [])]);
        Assert.That(() => new SchemaScriptRenderer(dialect).Plan(schema), Throws.TypeOf<ColumnDefaultPlanningException>());

        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.accdb");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "MsAccess", "DubUrl.accdb"), file);
        try
        {
            using (var connection = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={file};OLE DB Services=-2"))
            {
                connection.Open();
                var table = Create(connection, access, identity, columns);
                Execute(connection, $"INSERT INTO {table} ([Id]) VALUES (1)");
                Execute(connection, $"INSERT INTO {table} ([Id]) VALUES (2)");
                var stamp = (DateTime)Scalar(connection, $"SELECT LocalStamp FROM {table} WHERE Id=1")!;
                Assert.That(stamp, Is.InRange(DateTime.Now.AddMinutes(-1), DateTime.Now.AddSeconds(1)));
                Assert.That(Scalar(connection, $"SELECT LocalDate FROM {table} WHERE Id=1"), Is.EqualTo(stamp.Date));
                var clock = (DateTime)Scalar(connection, $"SELECT LocalTime FROM {table} WHERE Id=1")!;
                Assert.That(clock.Date, Is.EqualTo(new DateTime(1899, 12, 30)));
                Assert.That((clock.TimeOfDay - stamp.TimeOfDay).Duration(), Is.LessThan(TimeSpan.FromSeconds(2)));
                Assert.That(Scalar(connection, $"SELECT GeneratedId FROM {table} WHERE Id=1"), Is.Not.EqualTo(
                    Scalar(connection, $"SELECT GeneratedId FROM {table} WHERE Id=2")));
                Execute(connection, $"DROP TABLE {table}");
            }

            using var odbc = new OdbcConnection($"Driver={{Microsoft Access Driver (*.mdb, *.accdb)}};Dbq={file}");
            odbc.Open();
            var command = new SchemaScriptRenderer(access).Plan(schema).Single();
            Assert.That(() => Execute(odbc, command.CommandText), Throws.TypeOf<OdbcException>());
        }
        finally
        {
            File.Delete(file);
        }
    }
}
