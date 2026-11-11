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
