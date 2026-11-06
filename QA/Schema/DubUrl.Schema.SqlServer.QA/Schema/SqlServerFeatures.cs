using System.Data.Common;
using Microsoft.Data.SqlClient;
using DubUrl.Discovery;
using DubUrl.Schema;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Provider.SqlServer.Discovery;

namespace DubUrl.Schema.SqlServer.QA;

public sealed partial class SqlServerBaselineTests
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
            ["RegexCheck"] = new(ExpectedOutcome.RendererRejects,"The published SQL Server renderer has no regular-expression check implementation.","Regular-expression"),
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
            ["NativeVarcharKey"] = FeatureExpectation.Supported,
            ["NativeCharKey"] = FeatureExpectation.Supported,
            ["ExpressionIndexDiagnostic"] = new(ExpectedOutcome.DatabaseRejects,"SQL Server rejects expression keys without a computed column.","102"),
            ["FilteredIndexDiagnostic"] = FeatureExpectation.Supported,
            ["AggregateViewDiagnostic"] = FeatureExpectation.Supported,
            ["MissingReferenceDiagnostic"] = FeatureExpectation.Supported,
            ["DiagnosticDetails"] = FeatureExpectation.Supported,
            ["PublicDeployment"] = FeatureExpectation.Supported,
            ["FluentDeployment"] = FeatureExpectation.Supported,
            ["CleanupAfterFailure"] = FeatureExpectation.Supported,
        };
    protected override void ExecuteScript(DbConnection connection,string script)
    {
        var statements=QaSql.Statements(script).ToArray();
        if (statements.Any(s=>s.TrimStart().StartsWith("CREATE VIEW",StringComparison.OrdinalIgnoreCase)))
            foreach(var statement in statements) QaSql.Execute(connection,statement);
        else QaSql.Execute(connection,script); // Preserve same-batch comment variable collision coverage.
    }
    protected override string DatabaseErrorCode(DbException exception) => ((SqlException)exception).Number.ToString();
    protected override string CurrentSchema(DbConnection connection) => "dbo";
    protected override DatabaseObjectName QualifiedName(DbConnection connection,string name) => new(name,schema:CurrentSchema(connection));
    protected override ISchemaMetadataReader? CreateMetadataReader() => new SqlServerMetadataReader();
    protected override int? ExpectedDiscoveredStringLength => 40;
    protected override GeneratedColumnStorage DiscoveryGenerationStorage => GeneratedColumnStorage.Stored;
    protected override PublicDatabase CreatePublicDatabase()
    {
        DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient",SqlClientFactory.Instance);
        return QaSql.Public("mssql",CreateConnection());
    }
    protected override string ExpressionIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ((UPPER({column})))";
    protected override string FilteredIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ({column}) WHERE {column}>0";
    protected override IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderColumns(connection,new SqlServerMetadataReader(),CurrentSchema(connection),table.Name);
    protected override IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderIndexes(connection,new SqlServerMetadataReader(),CurrentSchema(connection),table.Name);
    protected override CommentFact ReadComments(DbConnection connection,DatabaseObjectName table) => QaSql.ReaderComments(connection,new SqlServerMetadataReader(),CurrentSchema(connection),table.Name);
    protected override bool ObjectExists(DbConnection connection,DatabaseObjectName name,bool view)
        => Convert.ToInt64(QaSql.Rows(connection,"SELECT COUNT(*) FROM sys.objects WHERE name=@name AND schema_id=SCHEMA_ID() AND type=@kind",("name",name.Name),("kind",view ? "V" : "U")).Single()[0])!=0;
}
