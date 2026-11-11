using System.Data;
using System.Data.Odbc;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;
using DubUrl.Authentication;
using DubUrl.Provider.Spark.QA;
using DubUrl.Providers.QA.Contracts.Connection;
using NUnit.Framework;

namespace DubUrl.Provider.Databricks.QA.Connection;

[Category("Live")]
public sealed class DatabricksOdbcTests : OdbcContract
{
    protected override string ConnectionUrl => DatabricksWarehouse.Url;
    protected override string AuthenticationConnectionUrl => DatabricksWarehouse.Location;
    protected override ConnectionUrl CreateLiveConnectionUrl()
        => base.CreateLiveConnectionUrl().WithAuthentication(DatabricksWarehouse.Authentication);
    protected override string SelectFirstCustomerSql => SparkOdbcCustomerQueries.First;
    protected override string SelectCustomerByIdSql => SparkOdbcCustomerQueries.ById;

    [Test, Category("ODBC")]
    public void ContextAndTimeDefaultsUseTheInsertSession()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var dialect = ((DatabricksSqlDialect)url.Dialect).WithTimeType();
            var name = new DatabaseObjectName("context_defaults", schema, catalog: catalog);
            var table = new Table(name,
            [
                new Column("id", DbType.Int32),
                new VarLengthColumn("effective_user", DbType.String, 320, defaultExpression: ColumnDefaultExpression.CurrentUser),
                new VarLengthColumn("session_identity", DbType.String, 320, defaultExpression: ColumnDefaultExpression.SessionUser),
                new Column("clock", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime()),
            ]);
            foreach (var command in new SchemaScriptRenderer(dialect).Plan(new DubUrl.Schema.Schema([table])))
                SparkSchemaQa.Execute(connection, command.CommandText);

            var quote = dialect.SchemaSqlRenderer;
            var target = quote.RenderObjectName(dialect, catalog, null, schema, table.Name);
            SparkSchemaQa.Execute(connection, $"USE SCHEMA {quote.RenderIdentifier(dialect, schema)}");
            SparkSchemaQa.Execute(connection, $"INSERT INTO {target} (id) VALUES (1)");
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE " +
                "effective_user = current_user() AND session_identity = session_user() AND clock IS NOT NULL"), Is.EqualTo(1));
            foreach (var expression in new[] { ColumnDefaultExpression.CurrentSchema, ColumnDefaultExpression.CurrentDatabase })
            {
                var incompatible = new Table(name,
                    [new VarLengthColumn("namespace", DbType.String, 255, defaultExpression: expression)]);
                Assert.That(() => new SchemaScriptRenderer(dialect).Plan(new DubUrl.Schema.Schema([incompatible])),
                    Throws.Exception.With.Message.Contains("captures namespace defaults before INSERT"));
            }
        });
    }

    [Test, Category("ODBC")]
    public void EvaluateColumnDefaultsOnInsert() => SparkSchemaQa.Defaults(CreateLiveConnectionUrl());

    [Test, Category("ODBC")]
    public void RenderTablesWithFacetsAndComments() => SparkSchemaQa.Tables(CreateLiveConnectionUrl(), delta: true);

    [Test, Category("ODBC")]
    public void OpenFreeEditionWarehouse()
        => OdbcSmokeTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance);

    [Test, Category("ODBC")]
    public void CreateDescribeAndDropUnityCatalogTable()
        => OdbcDdlTest.Verify(CreateLiveConnectionUrl(), OdbcFactory.Instance, "qa-table", "SeLeCt");
}

internal static class DatabricksWarehouse
{
    internal static ConnectionAuthentication Authentication
        => ConnectionAuthentication.UsernamePassword("token", RequireVariable("DATABRICKS_TOKEN"));

    internal static string Url => Location + "?AuthMech=3";

    internal static string Location
    {
        get
        {
            var host = RequireVariable("DATABRICKS_HOST");
            var warehouseId = RequireVariable("DATABRICKS_WAREHOUSE_ID");

            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                Assert.Fail("DATABRICKS_HOST must be the warehouse hostname.");
            if (!warehouseId.All(char.IsAsciiLetterOrDigit))
                Assert.Fail("DATABRICKS_WAREHOUSE_ID must contain only the warehouse ID.");

            return $"odbc+databricks://{host}/warehouse/{warehouseId}";
        }
    }

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Fail($"Set {name} before running Databricks QA.");
        return value!;
    }
}
