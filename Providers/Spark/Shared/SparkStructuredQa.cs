using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Schema.Documents;
using NUnit.Framework;
using static DubUrl.Provider.Spark.QA.SparkSchemaQa;

namespace DubUrl.Provider.Spark.QA;

internal static class SparkStructuredQa
{
    private static readonly ScalarType Integer = new(DbType.Int32);
    private static readonly ScalarType Text = new(DbType.String);
    private static readonly ScalarType Boolean = new(DbType.Boolean);

    internal static Task Recursive(ConnectionUrl url, bool delta)
        => WithScratchAsync(url, async (connection, catalog, schema) =>
        {
            var name = new DatabaseObjectName("structured", schema, catalog: catalog);
            var payload = Native(new StructType([
                new("items, list", new ArrayType(new MapType(Text, new StructType([
                    new("count`", Integer, true), new("select", Boolean, true),
                ]), true), true), true),
                new("name.dot", Text, true),
            ]));
            var json = new StructuredColumnType(new JsonType(), StructuredStorage.JsonText);
            const string description = "Caller’s JSON text and \\ escapes";
            var model = new DubUrl.Schema.Schema([new Table(name, [
                new Column("id", DbType.Int32),
                new Column("payload", payload, constraints: [new NullableConstraint()]),
                new Column("document", json, description: description),
                new Column("plain", DbType.String),
            ])]);

            var dialect = delta ? ((DatabricksSqlDialect)url.Dialect).WithNameBasedColumnMapping() : url.Dialect;
            var target = dialect.SchemaSqlRenderer.RenderObjectName(dialect, catalog, null, schema, name.Name);
            void Deploy(DubUrl.Schema.Schema value, SchemaCreationOptions options = SchemaCreationOptions.None)
                => new SchemaScriptDeployer().DeploySchema(url, new SchemaScriptRenderer(dialect, options).Plan(value));

            Deploy(model);
            var populated = "named_struct('items, list', array(map('one', named_struct('count`', 7, 'select', CAST(NULL AS BOOLEAN)), " +
                "'empty', NULL), NULL), 'name.dot', 'example')";
            Execute(connection, $"INSERT INTO {target} VALUES (1, {populated}, '{{\"a\":1}}', '{{\"a\":1}}'), " +
                "(2, NULL, 'invalid JSON', NULL), (3, NULL, NULL, NULL)");
            var items = "payload.`items, list`";
            var entry = $"element_at(element_at({items}, 1), 'one')";
            Assert.That(Scalar(connection, $"SELECT size({items}) FROM {target} WHERE id=1"), Is.EqualTo(2));
            Assert.That(Scalar(connection, $"SELECT {entry}.`count``` FROM {target} WHERE id=1"), Is.EqualTo(7));
            Assert.That(Scalar(connection, $"SELECT to_json({entry}) FROM {target} WHERE id=1"), Is.EqualTo("{\"count`\":7}"));
            Assert.That(Scalar(connection, $"SELECT payload.`name.dot` FROM {target} WHERE id=1"), Is.EqualTo("example"));
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE id=1 AND {entry}.`select` IS NULL " +
                $"AND element_at({items}, 2) IS NULL AND element_at(element_at({items}, 1), 'empty') IS NULL"), Is.EqualTo(1));
            Assert.That(Scalar(connection, $"SELECT document FROM {target} WHERE id=2"), Is.EqualTo("invalid JSON"));
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE id=3 AND payload IS NULL AND document IS NULL"), Is.EqualTo(1));
            Assert.That(() => Execute(connection, $"INSERT INTO {target} VALUES (4, " +
                "named_struct('items, list', array(map(CAST(NULL AS STRING), named_struct('count`', 7, 'select', true))), 'name.dot', 'x'), NULL, NULL)"),
                Throws.InstanceOf<DbException>());
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE id=4"), Is.EqualTo(0));

            var options = Scope(name, SchemaFeature.Comments);
            var discovered = await Inspect(url, options);
            var columns = discovered.Tables.Values.Single().Columns;
            Assert.That(columns["payload"].StructuredType, Is.EqualTo(payload));
            Assert.That(columns["document"].StructuredType, Is.EqualTo(json));
            Assert.That(columns["document"].Description, Is.EqualTo(description));
            Assert.That(columns["plain"].StructuredType, Is.Null);
            await RoundTrip(discovered, options, url, value => Deploy(value, SchemaCreationOptions.DropIfExists));
        });

    internal static Task DeltaGuarantees(ConnectionUrl url)
        => WithScratchAsync(url, async (connection, catalog, schema) =>
        {
            var name = new DatabaseObjectName("guarantees", schema, catalog: catalog);
            var required = Native(new StructType([
                new("inner", new StructType([new("number", Integer, false), new("optional", Text, true)]), false),
                new("also_optional", Text, true),
            ]));
            var variant = new StructuredColumnType(new JsonType(), StructuredStorage.Json);
            var vector = Native(new VectorType(DbType.Single, 3));
            var array = Native(new ArrayType(new ScalarType(DbType.Single), true));
            var model = new DubUrl.Schema.Schema([new Table(name, [
                new Column("id", DbType.Int32),
                new Column("required", required, constraints: [new NotNullableConstraint()]),
                new Column("variant_value", variant),
                new Column("embedding", vector),
                new Column("ordinary_array", array),
            ])]);
            var dialect = ((DatabricksSqlDialect)url.Dialect).WithVariantType();
            var target = dialect.SchemaSqlRenderer.RenderObjectName(dialect, catalog, null, schema, name.Name);
            void Deploy(DubUrl.Schema.Schema value, SchemaCreationOptions options = SchemaCreationOptions.None)
                => new SchemaScriptDeployer().DeploySchema(url, new SchemaScriptRenderer(dialect, options).Plan(value));
            Deploy(model);

            string Required(string number) => $"named_struct('inner', named_struct('number', {number}, 'optional', NULL), 'also_optional', NULL)";
            void Insert(int id, string structValue, string variantValue, string embedding, string ordinary = "NULL")
                => Execute(connection, $"INSERT INTO {target} VALUES ({id}, {structValue}, {variantValue}, {embedding}, {ordinary})");

            Insert(1, Required("7"), "parse_json('{\"z\":[1,null],\"a\":true}')", "array(1F, 2F, 3F)", "array(1F, NULL)");
            Insert(2, Required("8"), "NULL", "NULL", "array(1F)");
            Insert(3, Required("9"), "parse_json('null')", "NULL");
            var json = (string)Scalar(connection, $"SELECT to_json(variant_value) FROM {target} WHERE id=1")!;
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse("{\"a\":true,\"z\":[1,null]}")), Is.True);
            Assert.That(Scalar(connection, $"SELECT to_json(variant_value) FROM {target} WHERE id=2"), Is.SameAs(DBNull.Value));
            Assert.That(Scalar(connection, $"SELECT to_json(variant_value) FROM {target} WHERE id=3"), Is.EqualTo("null"));
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE id=3 AND variant_value IS NOT NULL AND is_variant_null(variant_value)"), Is.EqualTo(1));
            Assert.That(Scalar(connection, $"SELECT required.inner.number FROM {target} WHERE id=1"), Is.EqualTo(7));
            Assert.That(Scalar(connection, $"SELECT size(embedding) FROM {target} WHERE id=1"), Is.EqualTo(3));
            Assert.That(Convert.ToSingle(Scalar(connection, $"SELECT element_at(embedding, 2) FROM {target} WHERE id=1")), Is.EqualTo(2F));
            Assert.That(() => Insert(4, Required("7"), "parse_json('not JSON')", "NULL"),
                Throws.InstanceOf<DbException>());
            Assert.That(() => Insert(5, Required("CAST(NULL AS INT)"), "NULL", "NULL"),
                Throws.InstanceOf<DbException>().With.Message.Contains("NOT_NULL"));
            Assert.That(() => Insert(6, "NULL", "NULL", "NULL"),
                Throws.InstanceOf<DbException>().With.Message.Contains("NOT_NULL"));
            Assert.That(() => Insert(7, Required("7"), "NULL", "array(1F, 2F)"),
                Throws.InstanceOf<DbException>().With.Message.Contains("DELTA_VIOLATE_CONSTRAINT"));
            Assert.That(() => Insert(8, Required("7"), "NULL", "array(1F, NULL, 3F)"),
                Throws.InstanceOf<DbException>().With.Message.Contains("DELTA_VIOLATE_CONSTRAINT"));
            Assert.That(Scalar(connection, $"SELECT COUNT(*) FROM {target} WHERE id>=4"), Is.EqualTo(0));

            var options = Scope(name, SchemaFeature.Checks);
            var discovered = await Inspect(url, options);
            var columns = discovered.Tables.Values.Single().Columns;
            Assert.That(columns["required"].StructuredType, Is.EqualTo(required));
            Assert.That(columns["required"].Constraints.Get<NotNullableConstraint>(), Is.Not.Null);
            Assert.That(columns["variant_value"].StructuredType, Is.EqualTo(variant));
            Assert.That(columns["embedding"].StructuredType, Is.EqualTo(vector));
            Assert.That(columns["ordinary_array"].StructuredType, Is.EqualTo(array));
            await RoundTrip(discovered, options, url, value => Deploy(value, SchemaCreationOptions.DropIfExists));
        });

    private static DiscoveryOptions Scope(DatabaseObjectName name, params SchemaFeature[] extra)
        => new() { Tables = [new(name.Name, name.Schema, Catalog: name.Catalog)],
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, .. extra] };

    private static async Task<DubUrl.Schema.Schema> Inspect(ConnectionUrl url, DiscoveryOptions options)
    {
        var result = await new SchemaInspector().InspectAsync(url, options);
        Assert.That(result.IsComplete, Is.True, string.Join(" / ", result.Diagnostics.Select(item => item.Message)));
        return result.Schema!;
    }

    private static async Task RoundTrip(DubUrl.Schema.Schema discovered, DiscoveryOptions options, ConnectionUrl url,
        Action<DubUrl.Schema.Schema> deploy)
    {
        foreach (var format in new[] { SchemaDocumentFormat.Json, SchemaDocumentFormat.Yaml })
        {
            var expected = Export(discovered, format);
            using var input = new MemoryStream(expected);
            var document = new SchemaDocumentReader().Read(input, format);
            Assert.That(new SchemaDocumentValidator().Validate(document), Is.Empty);
            deploy(new SchemaDocumentMapper().Map(document));
            Assert.That(Export(await Inspect(url, options), format), Is.EqualTo(expected));
        }
    }

    private static byte[] Export(DubUrl.Schema.Schema model, SchemaDocumentFormat format)
    {
        using var output = new MemoryStream();
        new SchemaDocumentWriter().Write(output, new SchemaDocumentExporter().Map(model), format);
        return output.ToArray();
    }

    private static StructuredColumnType Native(StructuredType type) => new(type, StructuredStorage.Native);
}
