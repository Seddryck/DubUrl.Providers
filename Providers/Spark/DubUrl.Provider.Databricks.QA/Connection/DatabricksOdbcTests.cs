using System.Data;
using System.Data.Common;
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
    protected override bool SupportsSchemaDiscovery => true;
    protected override Task VerifySchemaDiscoveryAsync(ConnectionUrl url)
        => SparkSchemaQa.VerifySharedDiscovery(url, scope => VerifySchemaDiscoveryAsync(url, scope));
    protected override string SelectFirstCustomerSql => SparkOdbcCustomerQueries.First;
    protected override string SelectCustomerByIdSql => SparkOdbcCustomerQueries.ById;

    [Test, Category("ODBC")]
    public Task InspectExportAndRedeploySchema() => SparkSchemaQa.InspectExportAndRedeploy(CreateLiveConnectionUrl(), delta: true);

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
            rows.Close();

            var externalParent = new Table(parentName, [a, b, code]);
            var external = new DubUrl.Schema.Schema([child], [], [], [externalParent]);
            var primaryOnly = ((DatabricksSqlDialect)url.Dialect).WithInformationalKeys();
            var gated = new SchemaScriptRenderer(primaryOnly, SchemaCreationOptions.DropIfExists).Plan(external);
            var failure = Assert.Catch<DbException>(() => new SchemaScriptDeployer().DeploySchema(url, gated))!;
            Assert.That(failure.Message, Does.Contain("WithInformationalKeys(supportsUnique: true)"));
            Assert.That(failure.Data["SchemaCommandIndex"], Is.EqualTo(1));
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {Name(childName)}"), Is.EqualTo(1));

            var replacement = new SchemaScriptRenderer(dialect, SchemaCreationOptions.DropIfExists).Plan(external);
            new SchemaScriptDeployer().DeploySchema(url, replacement);
            SparkSchemaQa.Execute(connection, $"INSERT INTO {Name(childName)} VALUES (99, 98, 97)");
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {Name(childName)}"), Is.EqualTo(1));
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {Name(parentName)}"), Is.EqualTo(2));
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
            var table = new Table(name,
            [
                new Column("score", DbType.Int32, constraints: [new CheckConstraint(value, ">=", new ValueExpression(0), "positive_score")]),
                new Column("tag", DbType.String),
                new Column("label", DbType.String, constraints:
                    [new RegexCheckConstraint(new ColumnIdentityExpression("label"), @"^O'Brien;\d+$", RegexNullBehavior.AllowNull, "label_shape")]),
            ],
            [
                new MembershipCheckConstraint(value, [1, 2, null], NullMembershipBehavior.MatchNull, "allowed_score"),
                new RegexCheckConstraint(new ColumnIdentityExpression("tag"), @"^\d+$", RegexNullBehavior.RejectNull, "numeric_tag"),
            ]);
            foreach (var command in new SchemaScriptRenderer(url.Dialect).Plan(new DubUrl.Schema.Schema([table])))
                SparkSchemaQa.Execute(connection, command.CommandText);

            var target = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, catalog, null, schema, table.Name);
            SparkSchemaQa.Execute(connection, $"INSERT INTO {target} VALUES (1, '123', NULL), (NULL, '456', 'O\\'Brien;42')");
            foreach (var rejected in new[] { "(-1, '123', NULL)", "(3, '123', NULL)", "(1, 'bad', NULL)", "(1, NULL, NULL)", "(1, '123', 'wrong')" })
                Assert.That(() => SparkSchemaQa.Execute(connection, $"INSERT INTO {target} VALUES {rejected}"),
                    Throws.InstanceOf<DbException>());
            Assert.That(SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {target}"), Is.EqualTo(2));
            using var properties = connection.CreateCommand();
            properties.CommandText = $"SHOW TBLPROPERTIES {target} ('delta.constraints.numeric_tag')";
            using var rows = properties.ExecuteReader();
            Assert.That(rows.Read(), Is.True);
            Assert.That(rows.GetString(1), Does.Contain("RLIKE").IgnoreCase);
            rows.Close();
            properties.CommandText = $"SHOW TBLPROPERTIES {target} ('delta.constraints.positive_score')";
            using var comparison = properties.ExecuteReader();
            Assert.That(comparison.Read(), Is.True);
            Assert.That(comparison.GetString(1), Does.Contain(">= 0"));
        });
    }

    [Test, Category("ODBC")]
    public void DeltaNamespaceDefaultsIgnoreTheInsertSessionSchema()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var dialect = url.Dialect;
            var quote = dialect.SchemaSqlRenderer;
            var target = quote.RenderObjectName(dialect, catalog, null, schema, "namespace_probe");
            var insertSchema = schema + "_insert";
            var insertScope = $"{quote.RenderIdentifier(dialect, catalog)}.{quote.RenderIdentifier(dialect, insertSchema)}";
            SparkSchemaQa.Execute(connection, $"CREATE SCHEMA {insertScope}");
            try
            {
                SparkSchemaQa.Execute(connection, $"USE SCHEMA {quote.RenderIdentifier(dialect, schema)}");
                SparkSchemaQa.Execute(connection, $"""
                    CREATE TABLE {target} (
                        id INT, schema_value STRING DEFAULT current_schema(), database_value STRING DEFAULT current_database()
                    ) USING DELTA TBLPROPERTIES ('delta.feature.allowColumnDefaults' = 'supported')
                    """);
                SparkSchemaQa.Execute(connection, $"USE SCHEMA {quote.RenderIdentifier(dialect, insertSchema)}");
                Assert.That(SparkSchemaQa.Scalar(connection, "SELECT current_database()"), Is.EqualTo(insertSchema));
                SparkSchemaQa.Execute(connection, $"INSERT INTO {target} (id) VALUES (1)");
                var storedSchema = SparkSchemaQa.Scalar(connection, $"SELECT schema_value FROM {target}");
                var storedDatabase = SparkSchemaQa.Scalar(connection, $"SELECT database_value FROM {target}");
                TestContext.WriteLine($"Stored current_schema() as '{storedSchema}' and current_database() as '{storedDatabase}'.");
                Assert.That(storedSchema, Is.Not.Null.And.Not.EqualTo(DBNull.Value).And.Not.EqualTo(insertSchema));
                Assert.That(storedDatabase, Is.EqualTo(storedSchema));
            }
            finally { SparkSchemaQa.Execute(connection, $"DROP SCHEMA IF EXISTS {insertScope} CASCADE"); }
        });
    }

    [Test, Category("ODBC")]
    public void DeltaCatalogDefaultIgnoresTheInsertSessionCatalog()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var dialect = url.Dialect;
            var quote = dialect.SchemaSqlRenderer;
            var target = quote.RenderObjectName(dialect, catalog, null, schema, "catalog_probe");
            var otherCatalog = DubUrl.Providers.QA.Contracts.Schema.QaSql.Rows(connection, "SHOW CATALOGS")
                .Select(row => row[0]).OfType<string>().FirstOrDefault(name => name != catalog && name is "samples" or "system");
            if (otherCatalog is null)
                Assert.Ignore("The catalog-default probe requires a second catalog visible to the QA principal.");

            SparkSchemaQa.Execute(connection, $"""
                CREATE TABLE {target} (id INT, catalog_value STRING DEFAULT current_catalog())
                USING DELTA TBLPROPERTIES ('delta.feature.allowColumnDefaults' = 'supported')
                """);
            SparkSchemaQa.Execute(connection, $"USE CATALOG {quote.RenderIdentifier(dialect, otherCatalog!)}");
            Assert.That(SparkSchemaQa.Scalar(connection, "SELECT current_catalog()"), Is.EqualTo(otherCatalog));
            SparkSchemaQa.Execute(connection, $"INSERT INTO {target} (id) VALUES (1)");
            var storedCatalog = SparkSchemaQa.Scalar(connection, $"SELECT catalog_value FROM {target}");
            TestContext.WriteLine($"Stored current_catalog() as '{storedCatalog}' after changing the session to '{otherCatalog}'.");
            Assert.That(storedCatalog, Is.Not.Null.And.Not.EqualTo(DBNull.Value).And.Not.EqualTo(otherCatalog));
        });
    }

    [Test, Category("ODBC")]
    public void DeltaRejectsNamedSequences()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var dialect = url.Dialect;
            var sequence = new DatabaseObjectName("id_sequence", schema, catalog: catalog);
            var quoted = dialect.SchemaSqlRenderer.RenderObjectName(dialect, catalog, null, schema, sequence.Name);
            var error = Assert.Catch<DbException>(() => SparkSchemaQa.Execute(connection, $"CREATE SEQUENCE {quoted} START WITH 1"))!;
            Assert.That(error.Message, Does.Contain("PARSE_SYNTAX_ERROR").Or.Contain("UNSUPPORTED"));
            var model = new DubUrl.Schema.Schema([new Table("sequence_default",
                [new Column("id", DbType.Int64, defaultExpression: ColumnDefaultExpression.NextSequenceValue(sequence))])]);
            Assert.That(() => new SchemaScriptRenderer(dialect).Plan(model),
                Throws.Exception.With.Message.Contains("external named-sequence defaults"));
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
            SparkSchemaQa.Execute(connection, "SET TIME ZONE '+05:00'");
            TimeOnly ReadClock(string sql) => TimeOnly.Parse((string)SparkSchemaQa.Scalar(connection, sql)!,
                System.Globalization.CultureInfo.InvariantCulture);
            var before = ReadClock("SELECT CAST(current_time() AS STRING)");
            SparkSchemaQa.Execute(connection, $"INSERT INTO {target} (id) VALUES (1)");
            var after = ReadClock("SELECT CAST(current_time() AS STRING)");
            var inserted = ReadClock($"SELECT CAST(clock AS STRING) FROM {target} WHERE id = 1");
            Assert.That(inserted == after || inserted.IsBetween(before, after), Is.True,
                $"Stored TIME {inserted:HH:mm:ss.ffffff} is outside the insert interval {before:HH:mm:ss.ffffff} to {after:HH:mm:ss.ffffff}.");
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
    public void ScriptUnqualifiedDeltaTableQualifiesEmptyAndAbsentComments()
    {
        var url = CreateLiveConnectionUrl();
        SparkSchemaQa.WithScratch(url, (connection, catalog, schema) =>
        {
            var dialect = url.Dialect;
            var quote = dialect.SchemaSqlRenderer;
            SparkSchemaQa.Execute(connection, $"USE SCHEMA {quote.RenderIdentifier(dialect, schema)}");
            var table = new Table("comment_table",
                [new Column("absent", DbType.Int32), new Column("empty", DbType.Int32, description: "")], description: "");
            var sql = url.ScriptSchema(new DubUrl.Schema.Schema([table]));
            Assert.That(sql, Does.Contain("USING DELTA;").And.Not.Contain(" COMMENT "));
            SparkSchemaQa.Execute(connection, sql);

            var info = $"{quote.RenderIdentifier(dialect, catalog)}.information_schema";
            var scope = $"WHERE table_schema = '{schema}' AND table_name = 'comment_table'";
            var tableComment = SparkSchemaQa.Scalar(connection, $"SELECT comment FROM {info}.tables {scope}");
            var emptyComment = SparkSchemaQa.Scalar(connection, $"SELECT comment FROM {info}.columns {scope} AND column_name = 'empty'");
            var absentComment = SparkSchemaQa.Scalar(connection, $"SELECT comment FROM {info}.columns {scope} AND column_name = 'absent'");
            Assert.That(tableComment, Is.EqualTo(DBNull.Value));
            Assert.That(emptyComment, Is.EqualTo(DBNull.Value));
            Assert.That(absentComment, Is.EqualTo(DBNull.Value));
        });
    }

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
