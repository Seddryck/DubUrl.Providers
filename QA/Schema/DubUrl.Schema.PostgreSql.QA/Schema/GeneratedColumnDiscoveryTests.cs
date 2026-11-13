using DubUrl.Discovery;
using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.QA;
using Npgsql;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.PostgreSql.QA;

[Category("PostgreSql")]
[Category("Schema")]
public sealed class GeneratedColumnDiscoveryTests
{
    [Test]
    public async Task PostgreSql17CatalogOnlyExposesStoredGeneratedColumns()
    {
        await using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        Assert.That(connection.PostgreSqlVersion.Major, Is.EqualTo(17));

        var name = "qa_generated_inspection_" + Guid.NewGuid().ToString("N");
        var target = $"public.{name}";
        try
        {
            var rejected = Assert.Throws<PostgresException>(() => Execute(connection,
                $"CREATE TABLE {target} (source int, copy int GENERATED ALWAYS AS (source) VIRTUAL)"));
            Assert.That(rejected!.SqlState, Is.EqualTo(PostgresErrorCodes.SyntaxError));

            Execute(connection, $"CREATE TABLE {target} (source int, copy int GENERATED ALWAYS AS (source) STORED)");
            Assert.That(Scalar(connection, $"SELECT attgenerated::text FROM pg_catalog.pg_attribute "
                + $"WHERE attrelid = '{target}'::regclass AND attname = 'copy'"), Is.EqualTo("s"));

            var identity = new MetadataName(name, "public");
            var metadata = await new PostgreSqlMetadataReader().ReadAsync(connection, new()
            {
                Tables = [identity],
                Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.Generation]
            }, CancellationToken.None);
            var generation = metadata.Tables.Single(table => table.Identity == identity).Columns
                .Single(column => column.Name == "copy").Generation;
            Assert.That(generation?.Stored, Is.True);
            Assert.That(generation?.Expression, Is.Not.Null);
        }
        finally
        {
            Execute(connection, $"DROP TABLE IF EXISTS {target}");
        }
    }
}
