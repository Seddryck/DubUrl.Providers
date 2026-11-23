using System.Data.Common;
using DuckDB.NET.Data;
using DubUrl.Discovery;
using DubUrl.Schema;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Provider.DuckDb.Discovery;

namespace DubUrl.Schema.DuckDb.QA;

public sealed partial class DuckDbBaselineTests
{
    protected override IReadOnlyDictionary<string,FeatureExpectation> FeatureExpectations { get; } =
        new Dictionary<string,FeatureExpectation>
        {
            ["ReplaceTable"] = FeatureExpectation.Supported,
            ["QuotedIdentifiers"] = FeatureExpectation.Supported,
            ["TableComments"] = FeatureExpectation.Supported,
            ["ColumnComments"] = FeatureExpectation.Supported,
            ["EmptyComments"] = FeatureExpectation.Supported,
            ["CommentsSameBatch"] = FeatureExpectation.Supported,
            ["QualifiedComments"] = FeatureExpectation.Supported,
            ["UnqualifiedComments"] = FeatureExpectation.Supported,
            ["ColumnTypes"] = FeatureExpectation.Supported,
            ["ColumnFacets"] = new(ExpectedOutcome.NotEnforced,"The engine retains numeric facets but does not enforce declared string length."),
            ["Nullability"] = FeatureExpectation.Supported,
            ["LiteralDefaults"] = FeatureExpectation.Supported,
            ["IdentityDefault"] = FeatureExpectation.Supported,
            ["IdentitySeedIncrement"] = FeatureExpectation.Supported,
            ["GeneratedStored"] = new(ExpectedOutcome.RendererRejects,"DuckDB only supports virtual generated columns.","stored"),
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
            ["RegexCheck"] = FeatureExpectation.Supported,
            ["SingleIndex"] = FeatureExpectation.Supported,
            ["CompositeIndex"] = FeatureExpectation.Supported,
            ["UniqueIndex"] = FeatureExpectation.Supported,
            ["IndexDirection"] = new(ExpectedOutcome.NotEnforced,"DuckDB does not retain descending index key order."),
            ["ProjectionView"] = FeatureExpectation.Supported,
            ["DiscoveryTablesColumns"] = FeatureExpectation.Supported,
            ["DiscoveryFacets"] = FeatureExpectation.Supported,
            ["DiscoveryPrimaryKey"] = FeatureExpectation.Supported,
            ["DiscoveryUnique"] = FeatureExpectation.Supported,
            ["DiscoveryForeignKey"] = FeatureExpectation.Supported,
            ["DiscoveryDefaults"] = FeatureExpectation.Supported,
            ["DiscoveryNullDefault"] = new(ExpectedOutcome.NullDefaultNotPreserved, "DuckDB exposes the NULL literal in column_default. DubUrl's catalog reader loses it."),
            ["DiscoveryIdentity"] = FeatureExpectation.Supported,
            ["DiscoveryChecks"] = FeatureExpectation.Supported,
            ["DiscoveryGeneration"] = FeatureExpectation.Supported,
            ["DiscoveryComments"] = FeatureExpectation.Supported,
            ["DiscoveryIndexes"] = new(ExpectedOutcome.NotEnforced,"DuckDB does not retain index sort directions in metadata."),
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
            ["FilteredIndexDiagnostic"] = new(ExpectedOutcome.DatabaseRejects,"DuckDB rejects partial indexes explicitly.","Invalid") {ErrorFragment="partial indexes"},
            ["AggregateViewDiagnostic"] = FeatureExpectation.Supported,
            ["MissingReferenceDiagnostic"] = FeatureExpectation.Supported,
            ["DiagnosticDetails"] = FeatureExpectation.Supported,
            ["PublicDeployment"] = FeatureExpectation.Supported,
            ["FluentDeployment"] = FeatureExpectation.Supported,
            ["CleanupAfterFailure"] = FeatureExpectation.Supported,
        };
    protected override void ExecuteScript(DbConnection connection,string script) => QaSql.Execute(connection,script);
    protected override string DatabaseErrorCode(DbException exception) => ((DuckDBException)exception).ErrorType.ToString();
    protected override string CurrentSchema(DbConnection connection) => "main";
    protected override DatabaseObjectName QualifiedName(DbConnection connection,string name) => new(name,schema:CurrentSchema(connection));
    protected override ISchemaMetadataReader? CreateMetadataReader() => new DuckDbMetadataReader();
    protected override int? ExpectedDiscoveredStringLength => null;
    protected override GeneratedColumnStorage DiscoveryGenerationStorage => GeneratedColumnStorage.Virtual;
    protected override PublicDatabase CreatePublicDatabase() => QaSql.FileDatabase("duckdb",DuckDBClientFactory.Instance,"DuckDB.NET.Data");
    protected override string ExpressionIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ((UPPER({column})))";
    protected override string FilteredIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ({column}) WHERE {column}>0";
    protected override IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderColumns(connection,new DuckDbMetadataReader(),CurrentSchema(connection),table.Name);
    protected override IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderIndexes(connection,new DuckDbMetadataReader(),CurrentSchema(connection),table.Name);
    protected override CommentFact ReadComments(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderComments(connection,new DuckDbMetadataReader(),CurrentSchema(connection),table.Name);
    protected override bool ObjectExists(DbConnection connection,DatabaseObjectName name,bool view)
        => Convert.ToInt64(QaSql.Rows(connection,"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name="+QaSql.Literal(name.Name)+" AND table_type="+QaSql.Literal(view ? "VIEW" : "BASE TABLE")).Single()[0])!=0;
}
