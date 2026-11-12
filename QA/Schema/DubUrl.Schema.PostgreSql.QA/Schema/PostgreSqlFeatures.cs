using System.Data.Common;
using Npgsql;
using DubUrl.Discovery;
using DubUrl.Schema;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Provider.PostgreSql.Discovery;

namespace DubUrl.Provider.PostgreSql.QA;

public sealed partial class PostgreSqlSchemaTests
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
            ["ColumnFacets"] = FeatureExpectation.Supported,
            ["Nullability"] = FeatureExpectation.Supported,
            ["LiteralDefaults"] = FeatureExpectation.Supported,
            ["IdentityDefault"] = FeatureExpectation.Supported,
            ["IdentitySeedIncrement"] = FeatureExpectation.Supported,
            ["GeneratedStored"] = FeatureExpectation.Supported,
            ["GeneratedVirtual"] = new(ExpectedOutcome.RendererRejects,"The published PostgreSQL renderer supports only stored generated columns.","virtual"),
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
            ["IndexDirection"] = FeatureExpectation.Supported,
            ["ProjectionView"] = FeatureExpectation.Supported,
            ["DiscoveryTablesColumns"] = FeatureExpectation.Supported,
            ["DiscoveryFacets"] = FeatureExpectation.Supported,
            ["DiscoveryPrimaryKey"] = FeatureExpectation.Supported,
            ["DiscoveryUnique"] = FeatureExpectation.Supported,
            ["DiscoveryForeignKey"] = FeatureExpectation.Supported,
            ["DiscoveryDefaults"] = FeatureExpectation.Supported,
            ["DiscoveryIdentity"] = FeatureExpectation.Supported,
            ["DiscoveryChecks"] = FeatureExpectation.Supported,
            ["DiscoveryGeneration"] = FeatureExpectation.Supported,
            ["DiscoveryComments"] = FeatureExpectation.Supported,
            ["DiscoveryIndexes"] = FeatureExpectation.Supported,
            ["DiscoveryViews"] = FeatureExpectation.Supported,
            ["DiscoveryTableFilter"] = FeatureExpectation.Supported,
            ["DiscoverySchemaFilter"] = FeatureExpectation.Supported,
            ["DiscoveryClosure"] = FeatureExpectation.Supported,
            ["DiscoveryPartial"] = FeatureExpectation.Supported,
            ["DiscoveryStrict"] = FeatureExpectation.Supported,
            ["DiscoveryRoundTrip"] = FeatureExpectation.Supported,
            ["NativeVarcharKey"] = new(ExpectedOutcome.RendererRejects,"The provider does not register these explicit native type names.","native type"),
            ["NativeCharKey"] = new(ExpectedOutcome.RendererRejects,"The provider does not register these explicit native type names.","native type"),
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
    protected override string DatabaseErrorCode(DbException exception) => ((PostgresException)exception).SqlState;
    protected override string CurrentSchema(DbConnection connection) => "public";
    protected override DatabaseObjectName QualifiedName(DbConnection connection,string name) => new(name,schema:CurrentSchema(connection));
    protected override ISchemaMetadataReader? CreateMetadataReader() => new PostgreSqlMetadataReader();
    protected override int? ExpectedDiscoveredStringLength => 40;
    protected override GeneratedColumnStorage DiscoveryGenerationStorage => GeneratedColumnStorage.Stored;
    protected override PublicDatabase CreatePublicDatabase()
    {
        DbProviderFactories.RegisterFactory("Npgsql",NpgsqlFactory.Instance);
        return QaSql.Public("pgsql",CreateConnection());
    }
    protected override string ExpressionIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ((UPPER({column})))";
    protected override string FilteredIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ({column}) WHERE {column}>0";
    protected override IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderColumns(connection,new PostgreSqlMetadataReader(),CurrentSchema(connection),table.Name);
    protected override IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderIndexes(connection,new PostgreSqlMetadataReader(),CurrentSchema(connection),table.Name);
    protected override CommentFact ReadComments(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderComments(connection,new PostgreSqlMetadataReader(),CurrentSchema(connection),table.Name);
    protected override bool ObjectExists(DbConnection connection,DatabaseObjectName name,bool view)
        => Convert.ToInt64(QaSql.Rows(connection,"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name=@name AND table_type=@kind",("name",name.Name),("kind",view ? "VIEW" : "BASE TABLE")).Single()[0])!=0;
}
