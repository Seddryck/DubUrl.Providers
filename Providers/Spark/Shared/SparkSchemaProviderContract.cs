using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Discovery;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA;

[Category("Live")]
public abstract class SparkSchemaProviderContract : ProviderContract
{
    private static readonly IReadOnlyDictionary<string, FeatureExpectation> SparkExpectations = Expectations(false);
    private static readonly IReadOnlyDictionary<string, FeatureExpectation> DeltaExpectations = Expectations(true);

    private DbConnection? control;
    private ConnectionUrl scoped = null!;
    private string catalog = null!;
    private string schema = null!;
    private string scope = null!;

    protected abstract ConnectionUrl UnscopedUrl { get; }
    protected abstract ConnectionUrl ScopedUrl(string catalog, string schema);
    protected abstract bool IsDelta { get; }
    protected override IReadOnlyDictionary<string, FeatureExpectation> FeatureExpectations => IsDelta ? DeltaExpectations : SparkExpectations;
    protected override SchemaCapabilities Capabilities => SchemaCapabilities.CreateTable | SchemaCapabilities.DropTableIfExists;
    protected override GeneratedColumnStorage DiscoveryGenerationStorage => GeneratedColumnStorage.Stored;
    protected override int? ExpectedDiscoveredStringLength => 40;

    [OneTimeSetUp]
    public void CreateScenarioSchema()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        control = (DbConnection)UnscopedUrl.Open();
        catalog = (string)SparkSchemaQa.Scalar(control, "SELECT current_catalog()")!;
        schema = "duburl_shared_" + Guid.NewGuid().ToString("N");
        scope = $"{QuoteIdentifier(catalog)}.{QuoteIdentifier(schema)}";
        SparkSchemaQa.Execute(control, $"CREATE SCHEMA {scope}");
        scoped = ScopedUrl(catalog, schema);
        using var probe = (DbConnection)scoped.Open();
        Assert.That(SparkSchemaQa.Scalar(probe, "SELECT current_schema()"), Is.EqualTo(schema));
    }

    [OneTimeTearDown]
    public void DropScenarioSchema()
    {
        if (control is null) return;
        try
        {
            if (scope is not null)
                SparkSchemaQa.Execute(control, $"DROP SCHEMA IF EXISTS {scope} CASCADE");
        }
        finally { control.Dispose(); }
    }

    protected override DbConnection CreateConnection() => (DbConnection)scoped.Connect();
    protected override IDialect CreateDialect() => scoped.Dialect is DatabricksSqlDialect delta
        ? delta.WithInformationalKeys(supportsUnique: true) : scoped.Dialect;
    protected override string QuoteIdentifier(string name)
        => UnscopedUrl.Dialect.Renderer.Render(name, "identity");
    protected override string NormalizeTestName(string name) => name.ToLowerInvariant();
    protected override string CurrentSchema(DbConnection connection) => schema;
    protected override DatabaseObjectName QualifiedName(DbConnection connection, string name) => new(name, schema, catalog: catalog);
    protected override DiscoveryOptions DiscoveryScope(string name) => new() { Catalogs = [catalog], Schemas = [name] };
    protected override ISchemaMetadataReader CreateMetadataReader() => IsDelta ? new DatabricksMetadataReader() : new SparkMetadataReader();
    protected override PublicDatabase CreatePublicDatabase() => new(scoped, () => { });
    protected override string DatabaseErrorCode(DbException exception) => exception.SqlState ?? string.Empty;
    protected override void ExecuteScript(DbConnection connection, string script) => SparkSchemaQa.Execute(connection, script);
    protected override IReadOnlyList<string> DeploymentCommands(SchemaScriptRenderer renderer, DubUrl.Schema.Schema model)
        => renderer.Plan(model).Select(command => command.CommandText).ToArray();
    protected override IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection, DatabaseObjectName table)
        => QaSql.ReaderColumns(connection, CreateMetadataReader(), schema, table.Name);
    protected override CommentFact ReadComments(DbConnection connection, DatabaseObjectName table)
        => QaSql.ReaderComments(connection, CreateMetadataReader(), schema, table.Name);
    protected override IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection, DatabaseObjectName table)
        => throw new NotSupportedException("These table providers do not expose secondary indexes.");
    protected override string ExpressionIndexSql(string index, string table, string column)
        => throw new NotSupportedException("These table providers do not support expression indexes.");
    protected override string FilteredIndexSql(string index, string table, string column)
        => throw new NotSupportedException("These table providers do not support filtered indexes.");

    protected override bool ObjectExists(DbConnection connection, DatabaseObjectName name, bool view)
    {
        if (IsDelta)
        {
            var tableType = view ? "= 'VIEW'" : "<> 'VIEW'";
            var result = SparkSchemaQa.Scalar(connection, $"SELECT COUNT(*) FROM {QuoteIdentifier(catalog)}.information_schema.tables " +
                $"WHERE table_schema = {QaSql.Literal(schema)} AND table_name = {QaSql.Literal(name.Name)} AND table_type {tableType}");
            return Convert.ToInt64(result) > 0;
        }
        var sql = view ? $"SHOW VIEWS IN {scope}" : $"SHOW TABLES IN {scope}";
        return QaSql.Rows(connection, sql).Any(row => string.Equals(Convert.ToString(row[1]), name.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, FeatureExpectation> Expectations(bool delta)
    {
        var expectations = ScenarioNames().ToDictionary(name => name, _ => FeatureExpectation.Supported, StringComparer.Ordinal);
        void Set(FeatureExpectation value, params string[] names)
        {
            foreach (var name in names) expectations[name] = value;
        }
        void Reject(string evidence, string diagnostic, params string[] names)
            => Set(new(ExpectedOutcome.RendererRejects, evidence, diagnostic), names);
        void Exclude(string evidence, params string[] names) => Set(new(ExpectedOutcome.Inapplicable, evidence), names);

        Exclude("The shared fixture uses a dot inside a literal table name. Both catalogs forbid it even when quoted.",
            "QuotedIdentifiers");
        Reject("The table provider has no ordinary secondary indexes.", "indexes",
            "SingleIndex", "CompositeIndex", "UniqueIndex", "IndexDirection", "DiscoveryIndexes");
        Exclude("These catalogs have no expression or filtered secondary index to inspect.",
            "ExpressionIndexDiagnostic", "FilteredIndexDiagnostic");

        if (delta)
        {
            Set(new(ExpectedOutcome.Informational, "Unity Catalog keys retain ordered metadata but allow duplicates and orphan rows."),
                "SinglePrimaryKey", "CompositePrimaryKey", "SingleUnique", "CompositeUnique", "ForeignKey", "CompositeForeignKey",
                "DependencyCreate", "DependencyDrop", "DiscoveryPrimaryKey", "DiscoveryUnique", "DiscoveryForeignKey", "DiscoveryClosure",
                "NativeVarcharKey", "NativeCharKey");
            Reject("The shared identity fixture declares INT, while Delta identity requires BIGINT.", "BIGINT",
                "IdentityDefault", "IdentitySeedIncrement", "DiscoveryIdentity");
            Reject("Delta only supports stored generated columns.", "stored generated", "GeneratedVirtual");
        }
        else
        {
            Reject("The built-in Spark file-table provider does not enforce NOT NULL.", "NOT NULL", "Nullability", "DiscoveryFacets");
            Reject("Spark 3.5 rejects non-NULL defaults on bounded CHAR or VARCHAR.", "non-NULL default", "LiteralDefaults", "DiscoveryDefaults");
            Reject("The built-in Spark file-table provider does not support key constraints.", "keys",
                "SinglePrimaryKey", "CompositePrimaryKey", "SingleUnique", "CompositeUnique", "ForeignKey", "CompositeForeignKey",
                "DependencyCreate", "DependencyDrop", "DiscoveryPrimaryKey", "DiscoveryUnique", "DiscoveryForeignKey", "DiscoveryClosure",
                "MissingReferenceDiagnostic", "NativeVarcharKey", "NativeCharKey", "IdentityDefault", "IdentitySeedIncrement", "DiscoveryIdentity");
            Reject("Generated columns are unavailable on the built-in Spark file-table provider.", "generated",
                "GeneratedStored", "GeneratedVirtual", "DiscoveryGeneration");
            Reject("The built-in Spark file-table provider does not enforce checks.", "CHECK",
                "ComparisonCheck", "MembershipCheck", "DiscoveryChecks");
            Reject("The built-in Spark file-table provider does not enforce regex checks.", "Delta", "RegexCheck");
        }

        return expectations;
    }
}
