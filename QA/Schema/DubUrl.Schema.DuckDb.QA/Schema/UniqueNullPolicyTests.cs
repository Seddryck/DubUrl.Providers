using System.Data;
using DuckDB.NET.Data;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.DuckDb.QA;

[TestFixture]
[Category("DuckDb")]
[Category("Schema")]
public sealed class UniqueNullPolicyTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task CompositeTupleEnforcementAndDiscovery(bool named)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await UniqueNullPolicyChecks.VerifyAsync(connection, Dialect(), new DuckDbMetadataReader(),
            new(UniqueNullPolicy.NullsDistinct, Schema: "main", Named: named, UsesIndex: named, CanReference: !named, DiscoverAsIndex: named),
            exception => exception is DuckDBException && exception.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void NullableNullsNotDistinctIsRejectedBeforeDeployment()
    {
        var a = new Column("a", DbType.Int32, constraints: [new NullableConstraint()]);
        var b = new Column("b", DbType.Int32, constraints: [new NullableConstraint()]);
        var table = new Table("unsupported_null_policy", [a, b],
            [new UniquenessConstraint([b, a], null, UniqueNullPolicy.NullsNotDistinct)]);
        Assert.That(() => new SchemaScriptRenderer(Dialect()).Plan(new([table])),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("cannot be enforced"));
    }

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        return builder.Build().Get<DuckDbDialect>();
    }
}
