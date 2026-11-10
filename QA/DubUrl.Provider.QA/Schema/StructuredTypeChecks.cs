using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using DubUrl.Discovery;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Providers.QA.Contracts.Schema;

public static class StructuredTypeChecks
{
    public static Task VerifyJsonAsync(DbConnection connection, IDialect dialect, IStructuredColumnMetadataReader reader,
        DatabaseObjectName identity)
        => VerifyAsync(connection, dialect, reader, identity, new(new JsonType(), StructuredStorage.Json),
            "v", "'{\"items\":[1,null,{\"ok\":true}]}'", "{\"items\":[1,null,{\"ok\":true}]}",
            "'{}'", "{}", "'null'", "null", invalid: "'not json'");

    public static async Task VerifyAsync(DbConnection connection, IDialect dialect, IStructuredColumnMetadataReader reader,
        DatabaseObjectName identity, StructuredColumnType type, string projection,
        string populated, string expected, string? empty = null, string? expectedEmpty = null,
        string? additional = null, string? expectedAdditional = null, string? invalid = null)
    {
        var table = Create(connection, dialect, identity,
            new Column("id", DbType.Int32), new Column("v", type, constraints: [new NullableConstraint()]));
        var idColumn = dialect.SchemaSqlRenderer.RenderIdentifier(dialect, "id");
        try
        {
            Execute(connection, $"INSERT INTO {table} VALUES (1, {populated})");
            Execute(connection, $"INSERT INTO {table} VALUES (2, NULL)");
            AssertJson(1, expected);
            Assert.That(Scalar(connection, $"SELECT {projection} FROM {table} WHERE {idColumn}=2"), Is.EqualTo(DBNull.Value));
            if (empty is not null)
            {
                Execute(connection, $"INSERT INTO {table} VALUES (3, {empty})");
                AssertJson(3, expectedEmpty!);
            }
            if (additional is not null)
            {
                Execute(connection, $"INSERT INTO {table} VALUES (4, {additional})");
                AssertJson(4, expectedAdditional!);
            }
            if (invalid is not null)
                Assert.Catch<DbException>(() => Execute(connection, $"INSERT INTO {table} VALUES (5, {invalid})"));

            var result = await new SchemaInspector().InspectAsync(connection, reader, new()
            {
                Tables = [new(identity.Name, identity.Schema, identity.Database)],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns]
            });
            Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => item.Message)));
            var columns = result.Schema!.Tables.Values.Single().Columns;
            Assert.That(columns.Values.Single(column => column.Name.Equals("id", StringComparison.OrdinalIgnoreCase)).StructuredType, Is.Null);
            Assert.That(columns.Values.Single(column => column.Name.Equals("v", StringComparison.OrdinalIgnoreCase)).StructuredType, Is.EqualTo(type));
        }
        finally
        {
            Execute(connection, $"DROP TABLE {table}");
        }

        void AssertJson(int id, string expectedJson)
        {
            var actual = Scalar(connection, $"SELECT {projection} FROM {table} WHERE {idColumn}={id}");
            Assert.That(actual, Is.TypeOf<string>());
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse((string)actual!), JsonNode.Parse(expectedJson)),
                Is.True, $"Expected {expectedJson}, received {actual}.");
        }
    }
}
