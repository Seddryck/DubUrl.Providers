using System.Data;
using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Discovery;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public sealed class CatalogDiagnosticFixtureTests
{
    [Test]
    public async Task CrateReportsEachRowWhosePathCannotBeDecoded()
    {
        using var connection = Catalog(sql => sql.Contains("information_schema.tables", StringComparison.Ordinal)
            ? Rows("schema_name table_name", ["doc", "items"])
            : sql.Contains("information_schema.columns", StringComparison.Ordinal)
                ? Rows("schema_name table_name column_name type_name nullable details",
                    ["doc", "items", "id", "integer", "NO", """{"name":"id","path":[]}"""],
                    ["doc", "items", "missing", "geo_point", "YES", null],
                    ["doc", "items", "data['broken']", "text", "YES", """{"name":"data","path":[1]}"""])
                : throw new AssertionException(sql));
        var options = new DiscoveryOptions { Features = [SchemaFeature.Columns, SchemaFeature.PrimaryKeys] };
        var reader = new CrateDbMetadataReader();
        var snapshot = await reader.ReadWithStructuredTypesAsync(connection, options, default);
        Assert.That(snapshot.Metadata.Tables.Single().Columns.Single().Name, Is.EqualTo("id"));
        Assert.That(snapshot.Metadata.Diagnostics.Select(d => (d.Code, d.Object, d.Member, d.NativeDefinition)),
            Is.EquivalentTo(new[]
            {
                ("SDI009", new MetadataName("items", "doc"), "missing", "geo_point"),
                ("SDI009", new MetadataName("items", "doc"), "data['broken']", "text"),
            }));
    }

    private static DbConnection Catalog(Func<string, DbDataReader> read)
    {
        var connection = new Mock<DbConnection>();
        connection.SetupGet(db => db.State).Returns(ConnectionState.Open);
        connection.Protected().Setup<DbCommand>("CreateDbCommand").Returns(() =>
        {
            var command = new Mock<DbCommand>();
            command.SetupProperty(item => item.CommandText);
            command.Protected().Setup<Task<DbDataReader>>("ExecuteDbDataReaderAsync",
                ItExpr.IsAny<CommandBehavior>(), ItExpr.IsAny<CancellationToken>())
                .Returns(() => Task.FromResult(read(command.Object.CommandText)));
            return command.Object;
        });
        return connection.Object;
    }

    private static DbDataReader Rows(string columns, params object?[][] rows)
    {
        var result = new DataTable();
        foreach (var column in columns.Split(' ')) result.Columns.Add(column, typeof(object));
        foreach (var row in rows) result.Rows.Add(row);
        return result.CreateDataReader();
    }
}
