using System.Data;
using System.Data.Odbc;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
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
    public void DeployProjectionViewsAndDiagnosePartialFailure() => SparkSchemaQa.ProjectionViews(CreateLiveConnectionUrl());

    [Test, Category("ODBC")]
    public void InformationalKeysPreserveOrderAndAllowDuplicatesAndOrphans()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var dialect = ((DatabricksSqlDialect)url.Dialect).WithInformationalKeys(supportsUnique: true);
            var parentName = new DatabaseObjectName("parents", schema, catalog: catalog);
            var childName = new DatabaseObjectName("children", schema, catalog: catalog);
            var a = new Column("a", DbType.Int32);
            var b = new Column("b", DbType.Int32);
            var code = new Column("code", DbType.Int32);
            var parent = new Table(parentName, [a, b, code],
                [new PrimaryKeyConstraint([b, a], "parent_pk"), new UniquenessConstraint([code], "parent_uq")]);
            var child = new Table(childName, [new Column("x", DbType.Int32), new Column("y", DbType.Int32), new Column("z", DbType.Int32)],
            [
                new DubUrl.Schema.Constraints.ForeignKeyConstraint("child_fk", ["y", "x"], parentName, ["b", "a"]),
                new DubUrl.Schema.Constraints.ForeignKeyConstraint("child_uq_fk", ["z"], parentName, ["code"]),
            ]);
            var model = new DubUrl.Schema.Schema([child, parent]);
            new SchemaScriptDeployer().DeploySchema(url, new SchemaScriptRenderer(dialect).Plan(model));

            string Name(DatabaseObjectName name) => dialect.SchemaSqlRenderer.RenderObjectName(dialect, catalog, null, schema, name.Name);
            SparkSchemaQa.Execute(connection, $"INSERT INTO {Name(parentName)} VALUES (1, 2, 3), (1, 2, 3)");
            SparkSchemaQa.Execute(connection, $"INSERT INTO {Name(childName)} VALUES (99, 98, 97)");
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {Name(parentName)}"), Is.EqualTo(2));
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {Name(childName)}"), Is.EqualTo(1));

            var metadataCatalog = dialect.SchemaSqlRenderer.RenderIdentifier(dialect, catalog);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT column_name FROM {metadataCatalog}.information_schema.key_column_usage " +
                $"WHERE constraint_schema = '{schema}' AND constraint_name = 'child_fk' ORDER BY ordinal_position";
            using var rows = command.ExecuteReader();
            var columns = new List<string>();
            while (rows.Read()) columns.Add(rows.GetString(0));
            Assert.That(columns, Is.EqualTo(new[] { "y", "x" }));
        });
    }


    [Test, Category("ODBC")]
    public void DeltaChecksEnforceComparisonMembershipAndRegex()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var name = new DatabaseObjectName("delta_checks", schema, catalog: catalog);
            var value = new ColumnIdentityExpression("score");
            var table = new Table(name, [new Column("score", DbType.Int32), new Column("tag", DbType.String)],
            [
                new CheckConstraint(value, ">=", new ValueExpression(0), "positive_score"),
                new MembershipCheckConstraint(value, [1, 2, null], NullMembershipBehavior.MatchNull, "allowed_score"),
                new RegexCheckConstraint(new ColumnIdentityExpression("tag"), @"^\d+$", RegexNullBehavior.RejectNull, "numeric_tag"),
            ]);
            foreach (var command in new SchemaScriptRenderer(url.Dialect).Plan(new DubUrl.Schema.Schema([table])))
                SparkSchemaQa.Execute(connection, command.CommandText);

            var target = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, catalog, null, schema, table.Name);
            SparkSchemaQa.Execute(connection, $"INSERT INTO {target} VALUES (1, '123'), (NULL, '456')");
            foreach (var rejected in new[] { "(-1, '123')", "(3, '123')", "(1, 'bad')", "(1, NULL)" })
                Assert.That(() => SparkSchemaQa.Execute(connection, $"INSERT INTO {target} VALUES {rejected}"),
                    Throws.InstanceOf<System.Data.Common.DbException>());
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {target}"), Is.EqualTo(2));
            using var properties = connection.CreateCommand();
            properties.CommandText = $"SHOW TBLPROPERTIES {target} ('delta.constraints.numeric_tag')";
            using var rows = properties.ExecuteReader();
            Assert.That(rows.Read(), Is.True);
            Assert.That(rows.GetString(1), Does.Contain("RLIKE").IgnoreCase);
        });
    }


    [Test, Category("ODBC")]
    public void DeltaIdentitySupportsExplicitValuesAndResetsOnReplacement()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var name = new DatabaseObjectName("identity_table", schema, catalog: catalog);
            var table = new Table(name,
            [
                new Column("seq", DbType.Int64, generation: new IdentityColumnGeneration(20, -2)),
                new Column("marker", DbType.Int32),
            ]);
            var model = new DubUrl.Schema.Schema([table]);
            var target = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, catalog, null, schema, table.Name);
            void Deploy(SchemaCreationOptions options)
            {
                foreach (var command in new SchemaScriptRenderer(url.Dialect, options).Plan(model))
                    SparkSchemaQa.Execute(connection, command.CommandText);
            }
            long Generate(int marker)
            {
                SparkSchemaQa.Execute(connection, $"INSERT INTO {target} (marker) VALUES ({marker})");
                return Convert.ToInt64(SparkSchemaQa.Scalar(connection, $"SELECT seq FROM {target} WHERE marker = {marker}"));
            }

            Deploy(SchemaCreationOptions.None);
            Assert.That(Generate(1), Is.EqualTo(20));
            Assert.That(Generate(2), Is.LessThan(20));
            SparkSchemaQa.Execute(connection, $"INSERT INTO {target} (seq, marker) VALUES (99, 3)");
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT seq FROM {target} WHERE marker = 3"), Is.EqualTo(99));
            Assert.That(SparkSchemaQa.Scalar(connection, $"SHOW CREATE TABLE {target}"),
                Does.Contain("GENERATED BY DEFAULT AS IDENTITY (START WITH 20 INCREMENT BY -2)"));
            Deploy(SchemaCreationOptions.DropIfExists);
            Assert.That(Generate(1), Is.EqualTo(20));
        });
    }

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
