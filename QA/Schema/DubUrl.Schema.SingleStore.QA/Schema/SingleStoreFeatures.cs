using System.Data.Common;
using SingleStoreConnector;
using DubUrl.Discovery;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Schema;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using System.Text.RegularExpressions;

namespace DubUrl.Provider.SingleStore.QA;

public sealed partial class SingleStoreSchemaTests
{
    protected override TablePhysicalOptions? PhysicalOptions(string scenario)
        => new(SchemaTableStorage.Rowstore, ["b"]);
    protected override TablePhysicalOptions? DiscoveryPhysicalOptions(string scenario)
        => scenario is "DiscoveryPrimaryKey" or "DiscoveryUnique" or "DiscoveryIndexes" ? new(SchemaTableStorage.Rowstore, ["b"]) : null;
    protected override Constraint[] DiscoveryUniqueConstraints(Column[] columns)
        => [new UniquenessConstraint([columns[1], columns[0]], "unique_pair"),
            new UniquenessConstraint([columns[0]], "unique_single")];
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
            ["IdentitySeedIncrement"] = new(ExpectedOutcome.RendererRejects,"The MySQL renderer rejects per-column seed/increment settings.","seed"),
            ["GeneratedStored"] = FeatureExpectation.Supported,
            ["GeneratedVirtual"] = new(ExpectedOutcome.RendererRejects,"SingleStore supports persisted computed columns only.","virtual"),
            ["SinglePrimaryKey"] = FeatureExpectation.Supported,
            ["CompositePrimaryKey"] = FeatureExpectation.Supported,
            ["SingleUnique"] = FeatureExpectation.Supported,
            ["CompositeUnique"] = FeatureExpectation.Supported,
            ["ForeignKey"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support enforced foreign key constraints.","2752"),
            ["CompositeForeignKey"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support enforced foreign key constraints.","2752"),
            ["DependencyCreate"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support enforced foreign key constraints.","2752"),
            ["DependencyDrop"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support enforced foreign key constraints.","2752"),
            ["ComparisonCheck"] = new(ExpectedOutcome.NotEnforced,"The pinned SingleStore engine accepts CHECK clauses without enforcing them."),
            ["MembershipCheck"] = new(ExpectedOutcome.NotEnforced,"The pinned SingleStore engine accepts CHECK clauses without enforcing them."),
            ["RegexCheck"] = new(ExpectedOutcome.NotEnforced,"The pinned SingleStore engine accepts CHECK clauses without enforcing them."),
            ["SingleIndex"] = FeatureExpectation.Supported,
            ["CompositeIndex"] = FeatureExpectation.Supported,
            ["UniqueIndex"] = FeatureExpectation.Supported,
            ["IndexDirection"] = FeatureExpectation.Supported,
            ["ProjectionView"] = FeatureExpectation.Supported,
            ["DiscoveryTablesColumns"] = FeatureExpectation.Supported,
            ["DiscoveryFacets"] = FeatureExpectation.Supported,
            ["DiscoveryPrimaryKey"] = FeatureExpectation.Supported,
            ["DiscoveryUnique"] = FeatureExpectation.Supported,
            ["DiscoveryForeignKey"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support enforced foreign keys.","2752"),
            ["DiscoveryDefaults"] = FeatureExpectation.Supported,
            ["DiscoveryIdentity"] = new(ExpectedOutcome.RendererRejects,"Per-column identity seed/increment cannot be represented.","seed"),
            ["DiscoveryChecks"] = new(ExpectedOutcome.DatabaseRejects,"The pinned SingleStore engine rejects this table-level CHECK declaration.","1706"),
            ["DiscoveryGeneration"] = FeatureExpectation.Supported,
            ["DiscoveryComments"] = FeatureExpectation.Supported,
            ["DiscoveryIndexes"] = FeatureExpectation.Supported,
            ["DiscoveryViews"] = FeatureExpectation.Supported,
            ["DiscoveryTableFilter"] = FeatureExpectation.Supported,
            ["DiscoverySchemaFilter"] = FeatureExpectation.Supported,
            ["DiscoveryClosure"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support enforced foreign keys.","2752"),
            ["DiscoveryPartial"] = FeatureExpectation.Supported,
            ["DiscoveryStrict"] = FeatureExpectation.Supported,
            ["DiscoveryRoundTrip"] = FeatureExpectation.Supported,
            ["NativeVarcharKey"] = FeatureExpectation.Supported,
            ["NativeCharKey"] = FeatureExpectation.Supported,
            ["ExpressionIndexDiagnostic"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support this expression-index form.","1064"),
            ["FilteredIndexDiagnostic"] = new(ExpectedOutcome.DatabaseRejects,"Predicate-filtered indexes are not supported.","1064"),
            ["AggregateViewDiagnostic"] = FeatureExpectation.Supported,
            ["MissingReferenceDiagnostic"] = new(ExpectedOutcome.DatabaseRejects,"SingleStore does not support enforced foreign keys.","2752"),
            ["DiagnosticDetails"] = FeatureExpectation.Supported,
            ["PublicDeployment"] = FeatureExpectation.Supported,
            ["FluentDeployment"] = FeatureExpectation.Supported,
            ["CleanupAfterFailure"] = FeatureExpectation.Supported,
        };
    protected override void ExecuteScript(DbConnection connection,string script) => QaSql.Execute(connection,script);
    protected override string DatabaseErrorCode(DbException exception) => ((SingleStoreException)exception).Number.ToString();
    // Native UNIQUE indexes are discovered as constraints; DiscoveryUnique verifies them.
    protected override bool DiscoveryIndexIsUnique => false;
    protected override DiscoveryOptions DiscoveryScope(string name) => new() { Databases = [name] };
    protected override string CurrentSchema(DbConnection connection) => connection.Database;
    protected override DatabaseObjectName QualifiedName(DbConnection connection,string name) => new(name,database:CurrentSchema(connection));
    protected override ISchemaMetadataReader? CreateMetadataReader() => new SingleStoreMetadataReader();
    protected override int? ExpectedDiscoveredStringLength => 40;
    protected override GeneratedColumnStorage DiscoveryGenerationStorage => GeneratedColumnStorage.Stored;
    protected override PublicDatabase CreatePublicDatabase()
    {
        DbProviderFactories.RegisterFactory("SingleStoreConnector",SingleStoreConnectorFactory.Instance);
        return QaSql.Public("singlestore",CreateConnection());
    }
    protected override string ExpressionIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ((UPPER({column})))";
    protected override string FilteredIndexSql(string index,string table,string column) => $"CREATE INDEX {index} ON {table} ({column}) WHERE {column}>0";
    protected override IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection,DatabaseObjectName table)
        => QaSql.Rows(connection,"SELECT COLUMN_NAME,DATA_TYPE,CHARACTER_MAXIMUM_LENGTH,NUMERIC_PRECISION,NUMERIC_SCALE,IS_NULLABLE FROM information_schema.columns WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@name ORDER BY ORDINAL_POSITION",("name",table.Name))
            .Select(r=>new ColumnFact(QaSql.Text(r[0]),QaSql.Text(r[1]),QaSql.Int(r[2]),QaSql.Int(r[3]),QaSql.Int(r[4]),QaSql.Text(r[5])=="YES")).ToArray();
    protected override IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection,DatabaseObjectName table)
    {
        // SingleStore STATISTICS.COLLATION is a character collation, not MySQL's A/D flag.
        var quoted = "`" + table.Name.Replace("`", "``") + "`";
        var definition = QaSql.Text(QaSql.Rows(connection, "SHOW CREATE TABLE " + quoted).Single()[1]);
        return QaSql.Rows(connection,"SELECT INDEX_NAME,COLUMN_NAME,SEQ_IN_INDEX,NON_UNIQUE FROM information_schema.statistics WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@name ORDER BY INDEX_NAME,SEQ_IN_INDEX",("name",table.Name))
            .Select(row =>
            {
                var name = QaSql.Text(row[0]);
                var column = QaSql.Text(row[1]);
                var key = Regex.Match(definition, @"(?:UNIQUE\s+)?KEY\s+`" + Regex.Escape(name.Replace("`", "``")) + @"`\s*\((?<columns>[^\r\n]+?)\)", RegexOptions.IgnoreCase);
                var descending = Regex.IsMatch(key.Groups["columns"].Value, "`" + Regex.Escape(column.Replace("`", "``")) + @"`\s+DESC\b", RegexOptions.IgnoreCase);
                return new IndexFact(name, column, Convert.ToInt32(row[2]), Convert.ToInt32(row[3]) == 0, descending);
            }).ToArray();
    }
    protected override CommentFact ReadComments(DbConnection connection,DatabaseObjectName table)
    {
        var description=QaSql.Text(QaSql.Rows(connection,"SELECT TABLE_COMMENT FROM information_schema.tables WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@name",("name",table.Name)).Single()[0]);
        var columns=QaSql.Rows(connection,"SELECT COLUMN_NAME,COLUMN_COMMENT FROM information_schema.columns WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@name",("name",table.Name));
        return new(string.IsNullOrEmpty(description) ? null : description,columns.ToDictionary(r=>QaSql.Text(r[0]),r=>(string?) (string.IsNullOrEmpty(QaSql.Text(r[1])) ? null : QaSql.Text(r[1]))));
    }
    protected override bool ObjectExists(DbConnection connection,DatabaseObjectName name,bool view)
        => Convert.ToInt64(QaSql.Rows(connection,"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@name AND table_type=@kind",("name",name.Name),("kind",view ? "VIEW" : "BASE TABLE")).Single()[0])!=0;
}
