using System.Data;
using SingleStoreConnector;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.SingleStore.QA;

[TestFixture]
[Category("SingleStore")]
[Category("Schema")]
public sealed class UniqueNullPolicyTests
{
    [TestCase(SchemaTableStorage.Rowstore)]
    [TestCase(SchemaTableStorage.Columnstore)]
    public async Task CompositeNullsDistinctEnforcesAndDiscoversInOrder(SchemaTableStorage storage)
    {
        using var connection = SingleStoreTestDatabase.CreateConnection();
        await connection.OpenAsync();
        await UniqueNullPolicyChecks.VerifyAsync(connection, Dialect(), new SingleStoreMetadataReader(),
            new(UniqueNullPolicy.NullsDistinct, Database: connection.Database, CanReference: false, PhysicalOptions: new(storage, ["b"])),
            exception => exception is SingleStoreException { Number: 1062 });
    }

    [Test]
    public void NullableNullsNotDistinctIsRejected()
        => UniqueNullPolicyChecks.RejectNullablePolicy(Dialect(), UniqueNullPolicy.NullsNotDistinct, new(SchemaTableStorage.Rowstore, ["b"]));

    [Test]
    public void ColumnstoreRejectsTwoUniqueKeysIncludingLegacyDeclarations()
    {
        var a = new Column("a", DbType.Int32);
        var b = new Column("b", DbType.Int32);
        var c = new Column("c", DbType.Int32);
        var table = new Table(new("unique_limit"), [a, b, c],
            [new UniquenessConstraint([b, a], "legacy"), new UniquenessConstraint([b, c], "explicit_key", UniqueNullPolicy.NullsDistinct)],
            null, new(SchemaTableStorage.Columnstore, ["b"]));
        Assert.That(() => new SchemaScriptRenderer(Dialect()).Plan(new([table])),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("columnstore supports only one"));
    }

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<SingleStoreDialect>(["singlestore"]);
        return builder.Build().Get<SingleStoreDialect>();
    }
}
