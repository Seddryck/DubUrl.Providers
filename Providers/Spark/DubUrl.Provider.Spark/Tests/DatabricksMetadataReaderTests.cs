using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Schema;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.Tests;

public sealed class DatabricksMetadataReaderTests
{
    [Test]
    public async Task SparkReadsColumnAnnotationsWithNativeCommentsAndFiltersTheStorageProperty()
    {
        using var connection = Connection(sql => sql switch
        {
            "SHOW NAMESPACES IN `spark_catalog`" => Rows("namespace", ["default"]),
            "SHOW VIEWS IN `spark_catalog`.`default`" => Rows("viewName"),
            "SHOW TABLES IN `spark_catalog`.`default`" => Rows("tableName", ["t"]),
            "DESCRIBE TABLE EXTENDED `spark_catalog`.`default`.`t`" =>
                Rows("col_name data_type comment", ["id", "int", "Identifier"], [null, null, null]),
            "SHOW TBLPROPERTIES `spark_catalog`.`default`.`t`" => Rows("key value",
                ["dbu.column.annotations.v1", """{"id":{"owner":"team"},"removed":{"owner":"old"}}"""], ["myapp.source", "erp"]),
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        });
        ISchemaMetadataReader reader = new SparkMetadataReader();
        var options = StructuredScope("spark_catalog", "default") with
        { Features = [SchemaFeature.Annotations, SchemaFeature.ColumnAnnotations, SchemaFeature.Comments] };
        var metadata = await reader.ReadAsync(connection, options);
        var table = metadata.Tables.Single();
        Assert.That(table.Annotations.Keys, Is.EqualTo(new[] { "myapp.source" }));
        Assert.That(table.Columns.Single().Comment, Is.EqualTo("Identifier"));
        Assert.That(table.Columns.Single().Annotations["owner"], Is.EqualTo("team"));
        Assert.That(metadata.Diagnostics.Single().Message, Does.Contain("missing column"));
    }

    [Test]
    public async Task ClosureReadsDetailsOnlyForSelectedAndReferencedTables()
    {
        var propertyQueries = new List<string>();
        using var connection = Connection(sql => sql switch
        {
            _ when sql.Contains("FROM system.information_schema.tables") => Rows("catalog_name schema_name table_name",
                ["c", "s", "child"], ["c", "other", "parent"], ["c", "other", "unrelated"]),
            _ when sql.Contains("FROM system.information_schema.columns") => Rows("catalog_name schema_name table_name column_name ordinal type_name nullable",
                ["c", "s", "child", "id", 1, "INT", "YES"], ["c", "other", "parent", "id", 1, "INT", "YES"],
                ["c", "other", "parent", "namespace", 2, "VARCHAR(255)", "YES"]),
            _ when sql.Contains("FROM system.information_schema.key_column_usage") => Rows(
                "catalog_name schema_name table_name column_name ordinal kind key_name target_catalog target_schema target_table target_column",
                ["c", "s", "child", "id", 1, "FOREIGN KEY", "fk", "c", "other", "parent", "id"]),
            "SHOW TBLPROPERTIES `c`.`s`.`child`" or "SHOW TBLPROPERTIES `c`.`other`.`parent`" => Properties(sql),
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        }, sql => sql switch
        {
            "SHOW CREATE TABLE `c`.`s`.`child`" => throw new CatalogAccessException(),
            "SHOW CREATE TABLE `c`.`other`.`parent`" =>
                "CREATE TABLE `c`.`other`.`parent` (`id` INT DEFAULT 1, `namespace` VARCHAR(255) DEFAULT current_catalog()) USING DELTA",
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        });

        var child = new MetadataName("child", "s", Catalog: "c");
        var parent = new MetadataName("parent", "other", Catalog: "c");
        ISchemaMetadataReader reader = new DatabricksMetadataReader();
        var metadata = await reader.ReadAsync(connection, new()
        {
            Tables = [child], IncludeReferencedTables = true,
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.ForeignKeys, SchemaFeature.Defaults,
                SchemaFeature.Checks, SchemaFeature.Annotations],
        });

        Assert.That(metadata.Tables.Select(table => table.Identity), Is.EquivalentTo(new[] { child, parent }));
        var parentMetadata = metadata.Tables.Single(table => table.Identity == parent);
        Assert.That(propertyQueries, Is.EquivalentTo(new[] { "SHOW TBLPROPERTIES `c`.`s`.`child`", "SHOW TBLPROPERTIES `c`.`other`.`parent`" }));
        Assert.That(parentMetadata.Annotations, Is.EquivalentTo(new Dictionary<string, string> { ["myapp.Source"] = @"team's\data" }));
        Assert.That(parentMetadata.Checks.Single().Name, Is.EqualTo("id_check"));
        Assert.That(parentMetadata.Columns.Single(column => column.Name == "id").Default, Is.EqualTo(1));
        Assert.That(parentMetadata.Columns.Single(column => column.Name == "namespace").Default, Is.Null);
        foreach (var feature in new[] { SchemaFeature.Checks, SchemaFeature.Defaults, SchemaFeature.Annotations })
        {
            Assert.That(metadata.Coverage[feature], Is.EqualTo(MetadataCoverage.Inaccessible));
            Assert.That(metadata.Diagnostics.Single(diagnostic => diagnostic.Feature == feature
                && diagnostic.Coverage == MetadataCoverage.Inaccessible).Object, Is.EqualTo(child));
        }
        Assert.That(metadata.Diagnostics.Any(diagnostic => diagnostic.Feature == SchemaFeature.Checks
            && diagnostic.Coverage == MetadataCoverage.Unrepresentable && diagnostic.Object == parent), Is.True);
        Assert.That(metadata.Diagnostics.Any(diagnostic => diagnostic.Feature == SchemaFeature.Defaults
            && diagnostic.Coverage == MetadataCoverage.Unrepresentable && diagnostic.Object == parent), Is.True);

        DbDataReader Properties(string sql)
        {
            propertyQueries.Add(sql);
            if (sql.Contains("`child`"))
                throw new CatalogAccessException();
            return Rows("key value", ["delta.constraints.id_check", "coalesce((id >= 0), true)"],
                ["delta.constraints.raw_check", "id >= 0"], ["delta.feature.allowColumnDefaults", "supported"],
                ["myapp.Source", @"team's\data"]);
        }
    }

    [TestCase("PARQUET", true)]
    [TestCase("delta", false)]
    public async Task SparkFiltersInternalPropertiesAndAppliesDeltaPrefixByTableFormat(string format, bool keepDeltaKey)
    {
        using var connection = Connection(sql => sql switch
        {
            "SHOW NAMESPACES IN `spark_catalog`" => Rows("namespace", ["default"]),
            "SHOW VIEWS IN `spark_catalog`.`default`" => Rows("viewName"),
            "SHOW TABLES IN `spark_catalog`.`default`" => Rows("tableName", ["t"]),
            "DESCRIBE TABLE EXTENDED `spark_catalog`.`default`.`t`" =>
                Rows("col_name data_type comment", ["id", "int", null], [null, null, null], ["Provider", format, null]),
            "SHOW TBLPROPERTIES `spark_catalog`.`default`.`t`" => Rows("key value",
                ["myapp.Source", ""], ["delta.appendOnly", "true"], ["transient_lastDdlTime", "42"], ["numRows", "2"]),
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        });
        ISchemaMetadataReader reader = new SparkMetadataReader();
        var options = StructuredScope("spark_catalog", "default") with { Features = [SchemaFeature.Annotations] };
        var metadata = await reader.ReadAsync(connection, options);
        var expected = new Dictionary<string, string> { ["myapp.Source"] = "" };
        if (keepDeltaKey)
            expected.Add("delta.appendOnly", "true");

        Assert.That(metadata.Coverage[SchemaFeature.Annotations], Is.EqualTo(MetadataCoverage.Available));
        Assert.That(metadata.Tables.Single().Annotations, Is.EquivalentTo(expected));
    }

    [Test]
    public async Task HiddenViewDefinitionAndForeignKeyTargetRetainVisibleInventory()
    {
        using var connection = Connection(sql => sql switch
        {
            _ when sql.Contains("FROM system.information_schema.tables") => Rows("catalog_name schema_name table_name", ["c", "s", "child"]),
            _ when sql.Contains("FROM system.information_schema.columns") => Rows("catalog_name schema_name table_name column_name ordinal type_name nullable",
                ["c", "s", "child", "id", 1, "INT", "YES"]),
            _ when sql.Contains("FROM system.information_schema.key_column_usage") => Rows(
                "catalog_name schema_name table_name column_name ordinal kind key_name", ["c", "s", "child", "id", 1, "FOREIGN KEY", "fk"]),
            _ when sql.Contains("FROM `c`.information_schema.views") => Rows("catalog_name schema_name table_name definition column_name ordinal type_name nullable",
                ["c", "s", "visible", null, "id", 1, "INT", "YES"], ["c", "other", "unrelated", null, "id", 1, "INT", "YES"]),
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        });

        ISchemaMetadataReader reader = new DatabricksMetadataReader();
        var metadata = await reader.ReadAsync(connection, new()
        {
            Catalogs = ["c"], Schemas = ["s"], IncludeReferencedTables = true,
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.ForeignKeys, SchemaFeature.Views],
        });

        Assert.That(metadata.Tables.Single().ForeignKeys, Is.Empty);
        Assert.That(metadata.Views.Single().Identity, Is.EqualTo(new MetadataName("visible", "s", Catalog: "c")));
        Assert.That(metadata.Views.Single().Projections, Is.Null);
        Assert.That(metadata.Coverage[SchemaFeature.Views], Is.EqualTo(MetadataCoverage.Inaccessible));
        Assert.That(metadata.Coverage[SchemaFeature.ForeignKeys], Is.EqualTo(MetadataCoverage.Inaccessible));
    }

    [Test]
    public async Task StructuredReadRetainsKnownAndUnknownJsonFlags()
    {
        using var connection = StructuredConnection("""
            {"columns":[{"name":"v","nullable":false,"type":{"name":"struct","fields":[
                {"name":"second field","type":{"name":"int"},"nullable":false},
                {"name":"first","type":{"name":"map","key_type":{"name":"string"},"value_type":{"name":"array",
                    "element_type":{"name":"date"},"element_nullable":false}},"nullable":true}
            ]}}]}
            """);
        IStructuredColumnMetadataReader reader = new DatabricksMetadataReader();
        var options = StructuredScope();
        var snapshot = await reader.ReadWithStructuredTypesAsync(connection, options, default);
        var descriptor = snapshot.StructuredColumns.Values.Single();
        Assert.That(snapshot.Metadata.Tables.Single().Columns.Single().Nullable, Is.False);
        Assert.That(descriptor.Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(descriptor.Type?.Shape, Is.EqualTo(new StructType([
            new("second field", new ScalarType(DbType.Int32), false),
            new("first", new MapType(new ScalarType(DbType.String), new ArrayType(new ScalarType(DbType.Date), false), null), true),
        ])));
        var result = await new SchemaInspector().InspectAsync(connection, reader, options);
        Assert.That(result.IsComplete, Is.False);
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Feature == SchemaFeature.Columns
            && diagnostic.Coverage == MetadataCoverage.Unrepresentable), Is.True);
    }

    [TestCase(null)]
    [TestCase("incomplete JSON")]
    public async Task MissingJsonRetainsTypeTextWithUnknownNestedNullability(string? json)
    {
        using var connection = StructuredConnection(json, "ARRAY<INT>");
        IStructuredColumnMetadataReader reader = new DatabricksMetadataReader();
        var snapshot = await reader.ReadWithStructuredTypesAsync(connection, StructuredScope(), default);
        Assert.That(snapshot.StructuredColumns.Values.Single(), Is.EqualTo(new StructuredColumnMetadata(
            new(new ArrayType(new ScalarType(DbType.Int32), null), StructuredStorage.Native), MetadataCoverage.Inaccessible)));
    }

    [Test]
    public async Task VectorNeedsEvidenceForBothGuarantees()
    {
        var property = "delta.constraints." + DeltaVectorSql.ConstraintName("v");
        var json = $$$"""
            {"provider":"delta","columns":[{"name":"v","nullable":true,"type":{"name":"array",
              "element_type":{"name":"float"},"element_nullable":true}}],
              "table_properties":{"{{{property}}}":"size(v) = 3"}}
            """;
        using var connection = StructuredConnection(json, "ARRAY<FLOAT>");
        IStructuredColumnMetadataReader reader = new DatabricksMetadataReader();
        var snapshot = await reader.ReadWithStructuredTypesAsync(connection, StructuredScope(), default);
        Assert.That(snapshot.StructuredColumns.Values.Single(), Is.EqualTo(new StructuredColumnMetadata(
            new(new ArrayType(new ScalarType(DbType.Single), true), StructuredStorage.Native), MetadataCoverage.Available)));
    }

    [Test]
    public async Task SparkReadsLiteralFieldsFromShowCreateAndParquetNullabilityFromDescribe()
    {
        using var connection = Connection(sql => sql switch
        {
            "SHOW NAMESPACES IN `spark_catalog`" => Rows("namespace", ["default"]),
            "SHOW VIEWS IN `spark_catalog`.`default`" => Rows("viewName"),
            "SHOW TABLES IN `spark_catalog`.`default`" => Rows("tableName", ["t"]),
            "DESCRIBE TABLE EXTENDED `spark_catalog`.`default`.`t`" => Rows("col_name data_type comment",
                ["v", "struct<b c:array<map<string,struct<a`b:int>>>,name:string>", null],
                ["x", "string", null], [null, null, null], ["Provider", "PARQUET", null]),
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        }, sql => sql switch
        {
            "SHOW CREATE TABLE `spark_catalog`.`default`.`t`" =>
                "CREATE TABLE t (v STRUCT<`b c`: ARRAY<MAP<STRING, STRUCT<`a``b`: INT>>>, name: STRING>, x STRING) USING PARQUET",
            _ => throw new AssertionException($"Unexpected scalar query {sql}"),
        });
        IStructuredColumnMetadataReader reader = new SparkMetadataReader();
        var snapshot = await reader.ReadWithStructuredTypesAsync(connection, StructuredScope("spark_catalog", "default"), default);
        Assert.That(snapshot.Metadata.Tables.Single().Columns.Select(column => column.Name), Is.EqualTo(new[] { "v", "x" }));
        var structure = new StructType([
            new("b c", new ArrayType(new MapType(new ScalarType(DbType.String),
                new StructType([new("a`b", new ScalarType(DbType.Int32), true)]), true), true), true),
            new("name", new ScalarType(DbType.String), true),
        ]);
        Assert.That(snapshot.StructuredColumns.Values.Single(), Is.EqualTo(new StructuredColumnMetadata(
            new(structure, StructuredStorage.Native), MetadataCoverage.Available)));
    }

    private static DiscoveryOptions StructuredScope(string catalog = "c", string schema = "s")
        => new() { Tables = [new("t", schema, Catalog: catalog)], Features = [SchemaFeature.Tables, SchemaFeature.Columns] };

    private static DbConnection StructuredConnection(string? json, string type = "STRUCT<n:INT>")
        => Connection(sql => sql switch
        {
            _ when sql.Contains("FROM `c`.information_schema.tables") => Rows("catalog_name schema_name table_name", ["c", "s", "t"]),
            _ when sql.Contains("FROM `c`.information_schema.columns") => Rows("catalog_name schema_name table_name column_name ordinal type_name nullable",
                ["c", "s", "t", "v", 1, type, "YES"]),
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        }, sql => sql == "DESCRIBE TABLE EXTENDED `c`.`s`.`t` AS JSON"
            ? json ?? throw new CatalogAccessException()
            : throw new AssertionException($"Unexpected scalar query {sql}"));

    private static DbConnection Connection(Func<string, DbDataReader> executeReader, Func<string, object>? executeScalar = null)
    {
        var connection = new Mock<DbConnection>();
        connection.SetupGet(instance => instance.State).Returns(ConnectionState.Open);
        connection.Protected().Setup<DbCommand>("CreateDbCommand").Returns(() =>
        {
            var command = new Mock<DbCommand>();
            command.SetupProperty(instance => instance.CommandText);
            command.Protected().Setup<Task<DbDataReader>>("ExecuteDbDataReaderAsync",
                ItExpr.IsAny<CommandBehavior>(), ItExpr.IsAny<CancellationToken>())
                .Returns(() => Task.FromResult(executeReader(command.Object.CommandText)));
            command.Setup(instance => instance.ExecuteScalarAsync(It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<object?>(executeScalar?.Invoke(command.Object.CommandText)
                    ?? throw new AssertionException($"Unexpected scalar query {command.Object.CommandText}")));
            return command.Object;
        });
        return connection.Object;
    }

    private static DbDataReader Rows(string columns, params object?[][] values)
    {
        var table = new DataTable();
        foreach (var column in columns.Split(' ')) table.Columns.Add(column, typeof(object));
        foreach (var row in values) table.Rows.Add(row);
        return table.CreateDataReader();
    }

    private sealed class CatalogAccessException : DbException;
}
