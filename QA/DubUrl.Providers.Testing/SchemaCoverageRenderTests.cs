using System.Data;
using System.Text.RegularExpressions;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public sealed class SchemaCoverageRenderTests
{
    private static IEnumerable<TestCaseData> ExplicitNullTargets()
    {
        string[] targets = ["Sqlite", "DuckDb", "PostgreSql", "PostgreSql-18", "SqlServer", "SqlServer-2025",
            "Oracle", "SingleStore", "MySql", "Spark", "Databricks", "Hive"];
        return targets.Select(target => new TestCaseData(target).SetName($"RendersExplicitNullDefault_{target}"));
    }

    [TestCaseSource(nameof(ExplicitNullTargets))]
    public void RendersExplicitNullDefault(string target)
    {
        var dialect = SchemaCoverageRejectionTests.TargetDialect(target);
        var table = new Table("null_defaults",
            [new Column("explicit_null", DbType.Int32, DBNull.Value), new Column("omitted", DbType.Int32)]);
        var createTable = new SchemaScriptRenderer(dialect).Plan(new DubUrl.Schema.Schema([table]))
            .Single(command => command.Operation == SchemaSqlOperation.CreateTable).CommandText;

        string Quote(string column) => Regex.Escape(dialect.SchemaSqlRenderer.RenderIdentifier(dialect, column));
        Assert.That(createTable, Does.Match($@"(?i){Quote("explicit_null")}\s+[^,]*\bDEFAULT\s+NULL\b"));
        Assert.That(createTable, Does.Not.Match($@"(?i){Quote("omitted")}\s+[^,]*\bDEFAULT\b"));
    }
}
