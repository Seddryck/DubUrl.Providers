using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Schema;

public static class TypedDefaultChecks
{
    public static string Create(DbConnection connection, IDialect dialect, DatabaseObjectName identity,
        params Column[] columns)
    {
        var schema = new DubUrl.Schema.Schema([new Table(identity, columns, [])]);
        foreach (var command in new SchemaScriptRenderer(dialect).Plan(schema))
            Execute(connection, command.CommandText);
        return UniqueNullPolicyChecks.SqlName(dialect, identity);
    }

    public static async Task<Table> DiscoverAsync(DbConnection connection, ISchemaMetadataReader reader, DatabaseObjectName table,
        bool inspectGeneration = true)
    {
        var inspected = await new SchemaInspector().InspectAsync(connection, reader, new()
        {
            Tables = [new(table.Name, table.Schema, table.Database)],
            Features = inspectGeneration
                ? [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Defaults, SchemaFeature.Generation]
                : [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Defaults]
        });
        Assert.That(inspected.IsComplete, Is.True, string.Join(", ", inspected.Diagnostics.Select(item => item.Message)));
        return inspected.Schema!.Tables.Values.Single();
    }

    public static void Execute(DbConnection connection, string sql) => QaSql.Execute(connection, sql);

    public static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
