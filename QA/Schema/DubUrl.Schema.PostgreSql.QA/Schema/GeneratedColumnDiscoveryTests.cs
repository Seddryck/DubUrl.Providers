using DubUrl.Provider.PostgreSql.Discovery;
using DubUrl.Provider.PostgreSql.QA;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using Npgsql;
using NUnit.Framework;
using static DubUrl.Providers.QA.Contracts.Schema.TypedDefaultChecks;

namespace DubUrl.Schema.PostgreSql.QA;

[Category("PostgreSql")]
[Category("Schema")]
public sealed class GeneratedColumnDiscoveryTests
{
    [Test]
    public async Task DiscoveryPreservesGeneratedColumnStorage()
    {
        await using var connection = new NpgsqlConnection(PostgreSqlTestDatabase.ProviderConnectionString);
        await connection.OpenAsync();
        var major = connection.PostgreSqlVersion.Major;
        if (Environment.GetEnvironmentVariable("DUBURL_POSTGRESQL_QA_MAJOR") is { Length: > 0 } expectedMajor)
            Assert.That(major.ToString(), Is.EqualTo(expectedMajor));
        Assert.That(major, Is.AnyOf(17, 18));

        var name = "qa_generated_inspection_" + Guid.NewGuid().ToString("N");
        var target = $"public.{name}";
        const string virtualColumn = "virtual_copy int GENERATED ALWAYS AS (source) VIRTUAL";
        try
        {
            if (major == 17)
            {
                var rejected = Assert.Throws<PostgresException>(() =>
                    Execute(connection, $"CREATE TABLE {target} (source int, {virtualColumn})"));
                Assert.That(rejected!.SqlState, Is.EqualTo(PostgresErrorCodes.SyntaxError));
            }

            var virtualDefinition = major == 18 ? ", " + virtualColumn : "";
            Execute(connection, $"CREATE TABLE {target} (source int, "
                + $"stored_copy int GENERATED ALWAYS AS (source) STORED{virtualDefinition})");

            var table = await DiscoverAsync(connection, new PostgreSqlMetadataReader(), new(name, "public"));
            Assert.That(table.Columns["source"].Generation, Is.Null);
            AssertGeneratedColumn(table.Columns["stored_copy"], GeneratedColumnStorage.Stored);
            if (major == 18)
                AssertGeneratedColumn(table.Columns["virtual_copy"], GeneratedColumnStorage.Virtual);
        }
        finally
        {
            Execute(connection, $"DROP TABLE IF EXISTS {target}");
        }
    }

    private static void AssertGeneratedColumn(Column column, GeneratedColumnStorage storage)
    {
        Assert.That(column.Generation, Is.TypeOf<GeneratedColumnGeneration>());
        var generation = (GeneratedColumnGeneration)column.Generation!;
        Assert.That(generation.Storage, Is.EqualTo(storage));
        Assert.That(generation.Expression, Is.TypeOf<ColumnIdentityExpression>()
            .With.Property(nameof(ColumnIdentityExpression.Name)).EqualTo("source"));
    }
}
