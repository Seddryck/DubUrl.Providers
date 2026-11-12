using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Discovery;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.Tests;

public sealed class DatabricksMetadataReaderTests
{
    [Test]
    public async Task ClosureReadsDetailsOnlyForSelectedAndReferencedTables()
    {
        using var connection = Connection(sql => sql switch
        {
            _ when sql.Contains("FROM system.information_schema.tables") => Rows("catalog_name schema_name table_name",
                ["c", "s", "child"], ["c", "other", "parent"], ["c", "other", "unrelated"]),
            _ when sql.Contains("FROM system.information_schema.columns") => Rows("catalog_name schema_name table_name column_name ordinal type_name nullable",
                ["c", "s", "child", "id", 1, "INT", "YES"], ["c", "other", "parent", "id", 1, "INT", "YES"]),
            _ when sql.Contains("FROM system.information_schema.key_column_usage") => Rows(
                "catalog_name schema_name table_name column_name ordinal kind key_name target_catalog target_schema target_table target_column",
                ["c", "s", "child", "id", 1, "FOREIGN KEY", "fk", "c", "other", "parent", "id"]),
            "SHOW TBLPROPERTIES `c`.`s`.`child`" => throw new CatalogAccessException(),
            "SHOW TBLPROPERTIES `c`.`other`.`parent`" => Rows("key value",
                ["delta.constraints.id_check", "coalesce((id >= 0), true)"], ["delta.constraints.raw_check", "id >= 0"]),
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        }, sql => sql switch
        {
            "SHOW CREATE TABLE `c`.`s`.`child`" => throw new CatalogAccessException(),
            "SHOW CREATE TABLE `c`.`other`.`parent`" => "CREATE TABLE `c`.`other`.`parent` (`id` INT DEFAULT 1) USING DELTA",
            _ => throw new AssertionException($"Unexpected catalog query {sql}"),
        });

        var child = new MetadataName("child", "s", Catalog: "c");
        var parent = new MetadataName("parent", "other", Catalog: "c");
        ISchemaMetadataReader reader = new DatabricksMetadataReader();
        var metadata = await reader.ReadAsync(connection, new()
        {
            Tables = [child], IncludeReferencedTables = true,
            Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.ForeignKeys, SchemaFeature.Defaults, SchemaFeature.Checks],
        });

        Assert.That(metadata.Tables.Select(table => table.Identity), Is.EquivalentTo(new[] { child, parent }));
        var parentMetadata = metadata.Tables.Single(table => table.Identity == parent);
        Assert.That(parentMetadata.Checks.Single().Name, Is.EqualTo("id_check"));
        Assert.That(parentMetadata.Columns.Single().Default, Is.EqualTo(1));
        foreach (var feature in new[] { SchemaFeature.Checks, SchemaFeature.Defaults })
        {
            Assert.That(metadata.Coverage[feature], Is.EqualTo(MetadataCoverage.Inaccessible));
            Assert.That(metadata.Diagnostics.Single(diagnostic => diagnostic.Feature == feature
                && diagnostic.Coverage == MetadataCoverage.Inaccessible).Object, Is.EqualTo(child));
        }
        Assert.That(metadata.Diagnostics.Any(diagnostic => diagnostic.Feature == SchemaFeature.Checks
            && diagnostic.Coverage == MetadataCoverage.Unrepresentable && diagnostic.Object == parent), Is.True);
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

    private static DbConnection Connection(Func<string, DbDataReader> executeReader, Func<string, object>? executeScalar = null)
    {
        var connection = new Mock<DbConnection>();
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
