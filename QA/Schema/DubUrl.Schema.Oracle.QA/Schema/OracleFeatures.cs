using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using DubUrl.Discovery;
using DubUrl.Schema;
using DubUrl.Providers.QA.Contracts.Schema;

namespace DubUrl.Schema.Oracle.QA;

public sealed partial class OracleBaselineTests
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
            ["IdentityDefault"] = new(ExpectedOutcome.RendererRejects,"The published Oracle dialect has no identity renderer.","Identity"),
            ["IdentitySeedIncrement"] = new(ExpectedOutcome.RendererRejects,"The published Oracle dialect has no identity renderer.","Identity"),
            ["GeneratedStored"] = new(ExpectedOutcome.RendererRejects,"The published Oracle dialect has no generated-column renderer.","Generated"),
            ["GeneratedVirtual"] = new(ExpectedOutcome.RendererRejects,"The published Oracle dialect has no generated-column renderer.","Generated"),
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
            ["DiscoveryTablesColumns"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryFacets"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryPrimaryKey"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryUnique"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryForeignKey"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryDefaults"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryIdentity"] = new(ExpectedOutcome.NoDiscovery,"No schema discovery capability is registered."),
            ["DiscoveryChecks"] = new(ExpectedOutcome.NoDiscovery,"No schema discovery capability is registered."),
            ["DiscoveryGeneration"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryComments"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryIndexes"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryViews"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryTableFilter"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoverySchemaFilter"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryClosure"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryPartial"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryStrict"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiscoveryRoundTrip"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["NativeVarcharKey"] = FeatureExpectation.Supported,
            ["NativeCharKey"] = FeatureExpectation.Supported,
            ["ExpressionIndexDiagnostic"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["FilteredIndexDiagnostic"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["AggregateViewDiagnostic"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["MissingReferenceDiagnostic"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["DiagnosticDetails"] = new(ExpectedOutcome.NoDiscovery,"The published provider does not register SchemaDiscoveryCapability; SDI001 is required."),
            ["PublicDeployment"] = FeatureExpectation.Supported,
            ["FluentDeployment"] = FeatureExpectation.Supported,
            ["CleanupAfterFailure"] = FeatureExpectation.Supported,
        };
    protected override void ExecuteScript(DbConnection connection,string script)
    { foreach(var statement in QaSql.Statements(script)) QaSql.Execute(connection,statement); }
    protected override string DatabaseErrorCode(DbException exception) => ((OracleException)exception).Number.ToString();
    protected override string CurrentSchema(DbConnection connection) => QaSql.Text(QaSql.Rows(connection,"SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM dual").Single()[0]);
    protected override DatabaseObjectName QualifiedName(DbConnection connection,string name) => new(name,schema:CurrentSchema(connection));
    protected override ISchemaMetadataReader? CreateMetadataReader() => null;
    protected override int? ExpectedDiscoveredStringLength => null;
    protected override GeneratedColumnStorage DiscoveryGenerationStorage => GeneratedColumnStorage.Stored;
    protected override PublicDatabase CreatePublicDatabase()
    {
        DbProviderFactories.RegisterFactory("Oracle.ManagedDataAccess",OracleClientFactory.Instance);
        return QaSql.Public("oracle",CreateConnection());
    }
    protected override string ExpressionIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ((UPPER({column})))";
    protected override string FilteredIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ({column}) WHERE {column}>0";
    protected override IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection,DatabaseObjectName table)
        => QaSql.Rows(connection,"SELECT COLUMN_NAME,DATA_TYPE,CHAR_LENGTH,DATA_PRECISION,DATA_SCALE,NULLABLE FROM USER_TAB_COLUMNS WHERE TABLE_NAME=:name ORDER BY COLUMN_ID",("name",table.Name))
            .Select(r=>new ColumnFact(QaSql.Text(r[0]),QaSql.Text(r[1]),QaSql.Int(r[2]),QaSql.Int(r[3]),QaSql.Int(r[4]),QaSql.Text(r[5])=="Y")).ToArray();
    protected override IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection,DatabaseObjectName table)
    {
        var rows=QaSql.Rows(connection,"SELECT c.INDEX_NAME,c.COLUMN_NAME,c.COLUMN_POSITION,i.UNIQUENESS,c.DESCEND FROM USER_IND_COLUMNS c JOIN USER_INDEXES i ON i.INDEX_NAME=c.INDEX_NAME WHERE c.TABLE_NAME=:name ORDER BY c.INDEX_NAME,c.COLUMN_POSITION",("name",table.Name));
        return rows.Select(r=>
        {
            var column=QaSql.Text(r[1]);
            if(column.StartsWith("SYS_NC",StringComparison.Ordinal))
            {
                using var command=(OracleCommand)connection.CreateCommand();
                command.InitialLONGFetchSize=-1;
                command.CommandText="SELECT COLUMN_EXPRESSION FROM USER_IND_EXPRESSIONS WHERE INDEX_NAME=:name AND COLUMN_POSITION=:position";
                command.Parameters.Add("name",OracleDbType.Varchar2).Value=r[0];
                command.Parameters.Add("position",OracleDbType.Int32).Value=r[2];
                column=QaSql.Text(command.ExecuteScalar()).Trim((char)34);
            }
            return new IndexFact(QaSql.Text(r[0]),column,Convert.ToInt32(r[2]),QaSql.Text(r[3])=="UNIQUE",QaSql.Text(r[4])=="DESC");
        }).ToArray();
    }
    protected override CommentFact ReadComments(DbConnection connection,DatabaseObjectName table)
        => new((string?)QaSql.Rows(connection,"SELECT COMMENTS FROM USER_TAB_COMMENTS WHERE TABLE_NAME=:name",("name",table.Name)).Single()[0],
            QaSql.Rows(connection,"SELECT COLUMN_NAME,COMMENTS FROM USER_COL_COMMENTS WHERE TABLE_NAME=:name",("name",table.Name)).ToDictionary(r=>QaSql.Text(r[0]),r=>(string?)r[1]));
    protected override bool ObjectExists(DbConnection connection,DatabaseObjectName name,bool view)
        => Convert.ToInt64(QaSql.Rows(connection,"SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME=:name AND OBJECT_TYPE=:kind",("name",name.Name),("kind",view ? "VIEW" : "TABLE")).Single()[0])!=0;
}
