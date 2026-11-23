using System.Data.Common;
using Microsoft.Data.Sqlite;
using DubUrl.Discovery;
using DubUrl.Schema;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Provider.Sqlite.Discovery;

namespace DubUrl.Provider.Sqlite.QA;

public sealed partial class SqliteSchemaTests
{
    protected override IReadOnlyDictionary<string,FeatureExpectation> FeatureExpectations { get; } =
        new Dictionary<string,FeatureExpectation>
        {
            ["ReplaceTable"] = FeatureExpectation.Supported,
            ["QuotedIdentifiers"] = FeatureExpectation.Supported,
            ["TableComments"] = new(ExpectedOutcome.NoEngineComments,"SQLite has no native table/column comments."),
            ["ColumnComments"] = new(ExpectedOutcome.NoEngineComments,"SQLite has no native table/column comments."),
            ["EmptyComments"] = new(ExpectedOutcome.NoEngineComments,"SQLite has no native table/column comments."),
            ["CommentsSameBatch"] = new(ExpectedOutcome.NoEngineComments,"SQLite has no native table/column comments."),
            ["QualifiedComments"] = new(ExpectedOutcome.NoEngineComments,"SQLite has no native table/column comments."),
            ["UnqualifiedComments"] = new(ExpectedOutcome.NoEngineComments,"SQLite has no native table/column comments."),
            ["ColumnTypes"] = FeatureExpectation.Supported,
            ["ColumnFacets"] = new(ExpectedOutcome.NotEnforced,"The engine retains numeric facets but does not enforce declared string length."),
            ["Nullability"] = FeatureExpectation.Supported,
            ["LiteralDefaults"] = FeatureExpectation.Supported,
            ["IdentityDefault"] = FeatureExpectation.Supported,
            ["IdentitySeedIncrement"] = new(ExpectedOutcome.RendererRejects,"SQLite supports only default identity seed and increment.","seed 1"),
            ["GeneratedStored"] = FeatureExpectation.Supported,
            ["GeneratedVirtual"] = FeatureExpectation.Supported,
            ["SinglePrimaryKey"] = FeatureExpectation.Supported,
            ["CompositePrimaryKey"] = FeatureExpectation.Supported,
            ["SingleUnique"] = FeatureExpectation.Supported,
            ["CompositeUnique"] = FeatureExpectation.Supported,
            ["ForeignKey"] = FeatureExpectation.Supported,
            ["CompositeForeignKey"] = FeatureExpectation.Supported,
            ["DependencyCreate"] = FeatureExpectation.Supported,
            ["DependencyDrop"] = FeatureExpectation.Supported,
            ["ComparisonCheck"] = FeatureExpectation.Supported,
            ["MembershipCheck"] = FeatureExpectation.Supported,
            ["RegexCheck"] = new(ExpectedOutcome.RendererRejects,"SQLite has no built-in regular expression check renderer.","Regular-expression"),
            ["SingleIndex"] = FeatureExpectation.Supported,
            ["CompositeIndex"] = FeatureExpectation.Supported,
            ["UniqueIndex"] = FeatureExpectation.Supported,
            ["IndexDirection"] = FeatureExpectation.Supported,
            ["ProjectionView"] = FeatureExpectation.Supported,
            ["DiscoveryTablesColumns"] = FeatureExpectation.Supported,
            ["DiscoveryFacets"] = FeatureExpectation.Supported,
            ["DiscoveryPrimaryKey"] = FeatureExpectation.Supported,
            ["DiscoveryUnique"] = FeatureExpectation.Supported,
            ["DiscoveryForeignKey"] = FeatureExpectation.Supported,
            ["DiscoveryDefaults"] = FeatureExpectation.Supported,
            ["DiscoveryNullDefault"] = new(ExpectedOutcome.NullDefaultNotPreserved, "SQLite exposes the NULL literal in dflt_value. DubUrl's catalog reader loses it."),
            ["DiscoveryIdentity"] = new(ExpectedOutcome.RendererRejects,"The discovery scenario requests non-default identity seed and increment.","seed 1"),
            ["DiscoveryChecks"] = FeatureExpectation.Supported,
            ["DiscoveryGeneration"] = FeatureExpectation.Supported,
            ["DiscoveryComments"] = new(ExpectedOutcome.NotEnforced,"SQLite reports unavailable generation expressions and unsupported comment metadata."),
            ["DiscoveryIndexes"] = FeatureExpectation.Supported,
            ["DiscoveryViews"] = FeatureExpectation.Supported,
            ["DiscoveryTableFilter"] = FeatureExpectation.Supported,
            ["DiscoverySchemaFilter"] = FeatureExpectation.Supported,
            ["DiscoveryClosure"] = FeatureExpectation.Supported,
            ["DiscoveryPartial"] = FeatureExpectation.Supported,
            ["DiscoveryStrict"] = FeatureExpectation.Supported,
            ["DiscoveryRoundTrip"] = FeatureExpectation.Supported,
            ["NativeVarcharKey"] = FeatureExpectation.Supported,
            ["NativeCharKey"] = FeatureExpectation.Supported,
            ["ExpressionIndexDiagnostic"] = FeatureExpectation.Supported,
            ["FilteredIndexDiagnostic"] = FeatureExpectation.Supported,
            ["AggregateViewDiagnostic"] = FeatureExpectation.Supported,
            ["MissingReferenceDiagnostic"] = FeatureExpectation.Supported,
            ["DiagnosticDetails"] = FeatureExpectation.Supported,
            ["PublicDeployment"] = FeatureExpectation.Supported,
            ["FluentDeployment"] = FeatureExpectation.Supported,
            ["CleanupAfterFailure"] = FeatureExpectation.Supported,
        };
    protected override void ExecuteScript(DbConnection connection,string script) => QaSql.Execute(connection,script);
    protected override string DatabaseErrorCode(DbException exception) => ((SqliteException)exception).SqliteErrorCode.ToString();
    protected override string CurrentSchema(DbConnection connection) => "main";
    protected override DiscoveryOptions DiscoveryScope(string name) => new() { Databases = [name] };
    protected override DatabaseObjectName QualifiedName(DbConnection connection,string name) => new(name,database:CurrentSchema(connection));
    protected override ISchemaMetadataReader? CreateMetadataReader() => new SqliteMetadataReader();
    protected override int? ExpectedDiscoveredStringLength => 40;
    protected override GeneratedColumnStorage DiscoveryGenerationStorage => GeneratedColumnStorage.Stored;
    protected override PublicDatabase CreatePublicDatabase() => QaSql.FileDatabase("sqlite",SqliteFactory.Instance,"Microsoft.Data.Sqlite",SqliteConnection.ClearAllPools);
    protected override string ExpressionIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ((UPPER({column})))";
    protected override string FilteredIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ({column}) WHERE {column}>0";
    protected override IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderColumns(connection,new SqliteMetadataReader(),null,table.Name,CurrentSchema(connection));
    protected override IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderIndexes(connection,new SqliteMetadataReader(),null,table.Name,CurrentSchema(connection));
    protected override CommentFact ReadComments(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderComments(connection,new SqliteMetadataReader(),null,table.Name,CurrentSchema(connection));
    protected override bool ObjectExists(DbConnection connection,DatabaseObjectName name,bool view)
        => Convert.ToInt64(QaSql.Rows(connection,"SELECT COUNT(*) FROM sqlite_schema WHERE name=@name AND type=@kind",("name",name.Name),("kind",view ? "view" : "table")).Single()[0])!=0;
}
