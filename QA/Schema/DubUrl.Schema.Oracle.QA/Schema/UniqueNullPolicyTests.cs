using System.Data;
using Oracle.ManagedDataAccess.Client;
using DubUrl.Provider.Oracle.QA;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using NUnit.Framework;

namespace DubUrl.Schema.Oracle.QA;

[TestFixture]
[Category("Oracle")]
[Category("Schema")]
public sealed class UniqueNullPolicyTests
{
    [Test]
    public void NativeCompositeAcceptsOnlyAllNullDuplicates()
    {
        VerifyLive(UniqueNullPolicy.Native, ["B", "A"], requiredA: false, (connection, name) =>
            UniqueNullPolicyChecks.VerifyTuples(connection, name, IsDuplicate,
                allowsPartialNullDuplicates: false, allowsAllNullDuplicates: true));
    }

    [Test]
    public void ExplicitNullsNotDistinctSupportsCompositeWithARequiredMember()
    {
        VerifyLive(UniqueNullPolicy.NullsNotDistinct, ["B", "A"], requiredA: true, (connection, name) =>
        {
            QaSql.Execute(connection, $"INSERT INTO {name} VALUES (10, NULL)");
            Assert.That(() => QaSql.Execute(connection, $"INSERT INTO {name} VALUES (10, NULL)"),
                Throws.TypeOf<OracleException>().With.Property("Number").EqualTo(1));
            QaSql.Execute(connection, $"INSERT INTO {name} VALUES (11, NULL)");
            Assert.That(() => QaSql.Execute(connection, $"INSERT INTO {name} VALUES (NULL, NULL)"),
                Throws.TypeOf<OracleException>().With.Property("Number").EqualTo(1400));
        });
    }

    [Test]
    public void ExplicitNullsDistinctSupportsNullableSingleMember()
    {
        VerifyLive(UniqueNullPolicy.NullsDistinct, ["B"], requiredA: false, (connection, name) =>
        {
            QaSql.Execute(connection, $"INSERT INTO {name} VALUES (10, NULL)");
            QaSql.Execute(connection, $"INSERT INTO {name} VALUES (10, NULL)");
            QaSql.Execute(connection, $"INSERT INTO {name} VALUES (10, 20)");
            Assert.That(() => QaSql.Execute(connection, $"INSERT INTO {name} VALUES (11, 20)"),
                Throws.TypeOf<OracleException>().With.Property("Number").EqualTo(1));
        });
    }

    [TestCase(UniqueNullPolicy.NullsDistinct)]
    [TestCase(UniqueNullPolicy.NullsNotDistinct)]
    public void ExplicitPoliciesRejectTwoNullableMembers(UniqueNullPolicy policy)
        => UniqueNullPolicyChecks.RejectNullablePolicy(Dialect(), policy);

    private static void VerifyLive(UniqueNullPolicy policy, string[] keyColumns, bool requiredA, Action<OracleConnection, string> verify)
    {
        using var connection = OracleTestDatabase.Open();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var a = new Column("A", DbType.Int32, constraints: requiredA ? [new NotNullableConstraint()] : [new NullableConstraint()]);
        var b = new Column("B", DbType.Int32, constraints: [new NullableConstraint()]);
        var constraint = "UQ_POLICY_" + suffix;
        var table = new Table("UNIQUE_TEST_" + suffix, [a, b],
            [new UniquenessConstraint(keyColumns.Select(column => column == "A" ? a : b).ToArray(), constraint, policy)]);
        var dialect = Dialect();
        var name = UniqueNullPolicyChecks.SqlName(dialect, table.Identity);
        var plan = new SchemaScriptRenderer(dialect).Plan(new([table]));
        Assert.That(plan.Select(command => command.Operation), Is.EqualTo(new[] { SchemaSqlOperation.CreateTable }));
        QaSql.Execute(connection, plan.Single().CommandText);
        try
        {
            verify(connection, name);
            var rows = QaSql.Rows(connection, """
                SELECT c.COLUMN_NAME, u.INDEX_NAME
                FROM USER_CONSTRAINTS u JOIN USER_CONS_COLUMNS c ON u.CONSTRAINT_NAME=c.CONSTRAINT_NAME
                WHERE u.TABLE_NAME=:table_name AND u.CONSTRAINT_NAME=:key_name AND u.CONSTRAINT_TYPE='U'
                ORDER BY c.POSITION
                """, ("table_name", table.Name), ("key_name", constraint));
            Assert.That(rows.Select(row => QaSql.Text(row[0])), Is.EqualTo(keyColumns));
            Assert.That(rows.Select(row => QaSql.Text(row[1])).Distinct(), Has.Exactly(1).Items);
        }
        finally { QaSql.Execute(connection, $"DROP TABLE {name}"); }
    }

    private static bool IsDuplicate(Exception exception) => exception is OracleException { Number: 1 };

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<OracleDialect>(["oracle"]);
        return builder.Build().Get<OracleDialect>();
    }
}
