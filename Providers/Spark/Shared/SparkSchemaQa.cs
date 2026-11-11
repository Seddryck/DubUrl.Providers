using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA;

internal static class SparkSchemaQa
{
    internal static void Tables(ConnectionUrl url, bool delta)
    {
        WithScratch(url, (connection, catalog, schema) =>
        {
            const string comment = "Café's comment; (\\)";
            var name = new DatabaseObjectName("schema_features", schema, catalog: catalog);
            var constraints = delta ? new IConstraint[] { new NotNullableConstraint() } : null;
            var table = new Table(name,
            [
                new Column("id", DbType.Int32, constraints: constraints, description: comment),
                new VarLengthColumn("text", DbType.String, 12),
                new NumericColumn("amount", DbType.Decimal, 10, 2),
            ], description: comment);

            foreach (var command in new SchemaScriptRenderer(url.Dialect).Plan(new DubUrl.Schema.Schema([table])))
                Execute(connection, command.CommandText);

            var target = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, catalog, null, schema, table.Name);
            using var describe = connection.CreateCommand();
            describe.CommandText = $"DESCRIBE TABLE EXTENDED {target}";
            using (var rows = describe.ExecuteReader())
            {
                Assert.That(rows.Read(), Is.True);
                Assert.That(rows.GetString(0), Is.EqualTo("id"));
                Assert.That(rows.GetString(2), Is.EqualTo(comment));
                Assert.That(rows.Read(), Is.True);
                Assert.That(rows.GetString(0), Is.EqualTo("text"));
                Assert.That(rows.Read(), Is.True);
                Assert.That(rows.GetString(1).Replace(" ", ""), Is.EqualTo("decimal(10,2)").IgnoreCase);
                var comments = new List<string>();
                while (rows.Read())
                    if (!rows.IsDBNull(0) && rows.GetString(0) == "Comment")
                        comments.Add(rows.GetString(1));
                Assert.That(comments, Is.EqualTo(new[] { comment }));
            }
            Execute(connection, $"INSERT INTO {target} VALUES (1, 'hello', 1.25)");
            if (delta)
                Assert.That(() => Execute(connection, $"INSERT INTO {target} VALUES (NULL, 'hello', 1.25)"), Throws.InstanceOf<DbException>());
        });
    }

    internal static void Defaults(ConnectionUrl url)
    {
        WithScratch(url, (connection, catalog, schema) =>
        {
            var name = new DatabaseObjectName("column_defaults", schema, catalog: catalog);
            var table = new Table(name,
            [
                new Column("id", DbType.Int32),
                new Column("text", DbType.String, "Café's value"),
                new Column("nil", DbType.Int32, DBNull.Value),
                new NumericColumn("amount", DbType.Decimal, 10, 2, 1.25m),
                new Column("local_stamp", DbType.DateTime2, defaultExpression: ColumnDefaultExpression.CurrentTimestamp(6)),
                new Column("utc_stamp", DbType.DateTime2, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp(6)),
                new Column("local_date", DbType.Date, defaultExpression: ColumnDefaultExpression.CurrentDate),
            ]);

            Execute(connection, "SET TIME ZONE '+05:00'");
            foreach (var command in new SchemaScriptRenderer(url.Dialect).Plan(new DubUrl.Schema.Schema([table])))
                Execute(connection, command.CommandText);

            var target = url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, catalog, null, schema, table.Name);
            Execute(connection, $"INSERT INTO {target} (id) VALUES (1)");
            Execute(connection, $"INSERT INTO {target} (id, text, nil, amount) VALUES (2, NULL, 5, 2.00)");
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE id = 1 AND text = 'Café\\'s value' " +
                "AND nil IS NULL AND amount = 1.25 AND CAST(local_stamp AS DATE) = local_date " +
                "AND timestampdiff(SECOND, utc_stamp, local_stamp) BETWEEN 17999 AND 18001"), Is.EqualTo(1));
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE id = 2 AND text IS NULL " +
                "AND nil = 5 AND amount = 2.00"), Is.EqualTo(1));
        });
    }

    internal static void ProjectionViews(ConnectionUrl url)
    {
        WithScratch(url, (connection, catalog, schema) =>
        {
            DatabaseObjectName Name(string name) => new(name, schema, catalog: catalog);
            string SqlName(string name) => url.Dialect.SchemaSqlRenderer.RenderObjectName(url.Dialect, catalog, null, schema, name);
            var source = Name("source_table");
            var first = Name("first_view");
            var second = Name("second_view");
            DubUrl.Schema.Builders.ISchemaBuilder Build(DubUrl.Schema.Builders.ITableCollectionBuilder builder)
                => builder.WithTables(tables => tables.Add(table => table.WithIdentity(source)
                    .WithColumns(columns => columns.Add(column => column.WithName("item").WithType(DbType.String)))))
                    .WithViews(views => views
                        .Add(view => view.WithIdentity(second).From(first)
                            .WithProjections(projections => projections.Add(projection => projection.WithColumn("folded"))))
                        .Add(view => view.WithIdentity(first).From(source)
                            .WithProjections(projections => projections
                                .Add(projection => projection.WithFunction("Lower", new ColumnIdentityExpression("item")).As("folded"))
                                .Add(projection => projection.WithValue("O'Brien;").As("literal")))));

            url.DeploySchema(Build(new DubUrl.Schema.Builders.SchemaBuilder()).Build());
            Execute(connection, $"INSERT INTO {SqlName(source.Name)} VALUES ('ABC')");
            Assert.That(Scalar(connection, $"SELECT folded FROM {SqlName(second.Name)}"), Is.EqualTo("abc"));
            Assert.That(Scalar(connection, $"SELECT literal FROM {SqlName(first.Name)}"), Is.EqualTo("O'Brien;"));

            url.DeploySchema(Build, SchemaCreationOptions.DropIfExists);
            // Spark can retain the old file listing on a session that read the pre-replacement table.
            using (var replacement = (DbConnection)url.Open())
                Assert.That(Scalar(replacement, $"SELECT COUNT(*) FROM {SqlName(second.Name)}"), Is.EqualTo(0));

            var newTable = new Table(Name("created_before_error"), [new Column("id", DbType.Int32)]);
            var alreadyExists = new Table(source, [new Column("item", DbType.String)]);
            var afterFailure = new View(Name("after_failure"), new ViewQuery(newTable.Identity,
                new ViewProjection(new ColumnIdentityExpression("id"))));
            var failure = Assert.Catch<DbException>(() => url.DeploySchema(
                new DubUrl.Schema.Schema([newTable, alreadyExists], [], [afterFailure])));
            Assert.That(failure!.Data["SchemaCommandIndex"], Is.EqualTo(1));
            Assert.That(failure.Data["SchemaCommandOperation"], Is.EqualTo("CreateTable"));
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {SqlName(newTable.Name)}"), Is.EqualTo(0));
            Assert.That(() => Scalar(connection, $"SELECT COUNT(*) FROM {SqlName(afterFailure.Name)}"),
                Throws.InstanceOf<DbException>());
        });
    }

    internal static void WithScratch(ConnectionUrl url, Action<DbConnection, string, string> run)
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        using var connection = (DbConnection)url.Open();
        var catalog = (string)Scalar(connection, "SELECT current_catalog()")!;
        var schema = "duburl_qa_" + Guid.NewGuid().ToString("N");
        var renderer = url.Dialect.SchemaSqlRenderer;
        var scope = $"{renderer.RenderIdentifier(url.Dialect, catalog)}.{renderer.RenderIdentifier(url.Dialect, schema)}";
        Execute(connection, $"CREATE SCHEMA {scope}");
        try { run(connection, catalog, schema); }
        finally { Execute(connection, $"DROP SCHEMA IF EXISTS {scope} CASCADE"); }
    }

    internal static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        return command.ExecuteScalar();
    }
}
