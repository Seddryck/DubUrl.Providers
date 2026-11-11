using System.Data;
using System.Data.Common;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Connection;

public static class OdbcDdlTest
{
    public static void Verify(ConnectionUrl url, DbProviderFactory factory, string tableName, string columnName,
        string? expectedCatalog = null)
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", factory);
        using var connection = Run("open connection", url.Open);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 60;

        var catalog = Run("read current catalog", () =>
        {
            command.CommandText = "SELECT current_catalog()";
            return (string)command.ExecuteScalar()!;
        });
        Assert.That(catalog, Is.Not.Null.And.Not.Empty);
        if (expectedCatalog is not null)
            Assert.That(catalog, Is.EqualTo(expectedCatalog));

        var scratchSchema = "duburl_qa_" + Guid.NewGuid().ToString("N");
        var dialect = url.Dialect;
        var schemaName = string.Join('.', new[] { catalog, scratchSchema }
            .Select(part => dialect.SchemaSqlRenderer.RenderIdentifier(dialect, part)));
        var qualifiedTable = dialect.SchemaSqlRenderer.RenderObjectName(dialect, catalog, null, scratchSchema, tableName);
        var model = new DubUrl.Schema.Schema([new Table(new DatabaseObjectName(tableName, scratchSchema, catalog: catalog),
            [new Column(columnName, DbType.Int32)])]);
        var plan = new SchemaScriptRenderer(dialect, SchemaCreationOptions.DropIfExists).Plan(model);
        var create = plan.Single(statement => statement.Operation == SchemaSqlOperation.CreateTable);
        var drop = plan.Single(statement => statement.Operation == SchemaSqlOperation.DropTable);

        int Execute(string operation, string sql) => Run(operation, () =>
        {
            command.CommandText = sql;
            return command.ExecuteNonQuery();
        });

        try
        {
            Execute("create scratch schema", $"CREATE SCHEMA {schemaName}");
            try
            {
                Execute("create scratch table", create.CommandText);
                Run("describe scratch table", () =>
                {
                    command.CommandText = $"DESCRIBE TABLE {qualifiedTable}";
                    using var reader = command.ExecuteReader();
                    Assert.That(reader.Read(), Is.True);
                    Assert.That(reader.GetString(0), Is.EqualTo(columnName));
                    Assert.That(reader.GetString(1), Is.EqualTo("int").IgnoreCase);
                    return true;
                });
            }
            finally
            {
                Execute("drop scratch table", drop.CommandText);
            }
        }
        finally
        {
            Execute("drop scratch schema", $"DROP SCHEMA IF EXISTS {schemaName}");
        }
    }

    private static T Run<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DbException exception)
        {
            // ODBC diagnostics may include credentials from the connection string.
            var state = exception.SqlState;
            var details = state?.Length == 5 && state.All(char.IsAsciiLetterOrDigit) ? $" (SQLSTATE {state})" : "";
            throw new AssertionException($"Live ODBC failed to {operation}{details}.");
        }
    }
}
