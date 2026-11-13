using System.Data;
using System.Text.Json;
using DubUrl.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Provider.Hive.Sql;
using DubUrl.Provider.MySql.Discovery;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Provider.Oracle.Discovery;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Provider.Sqlite.Discovery;
using DubUrl.Provider.Sqlite.Sql;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public sealed class SchemaCoverageRejectionTests
{
    private const string ColumnName = "feature_value";
    private static readonly ScalarType Integer = new(DbType.Int32);

    private static IEnumerable<TestCaseData> RendererRejections() => Rejections("rendererRejections", "Renderer");
    private static IEnumerable<TestCaseData> InspectionRejections() => Rejections("inspectionRejections", "Inspection");

    private static IEnumerable<TestCaseData> Rejections(string section, string operation)
    {
        using var stream = typeof(SchemaCoverageRejectionTests).Assembly
            .GetManifestResourceStream("DubUrl.SchemaCoverageCases")!;
        using var config = JsonDocument.Parse(stream);
        foreach (var rejection in config.RootElement.GetProperty(section).EnumerateArray())
        foreach (var target in rejection.GetProperty("targets").EnumerateArray())
        foreach (var feature in rejection.GetProperty("features").EnumerateArray())
        {
            var targetId = target.GetString()!;
            var featureId = feature.GetString()!;
            yield return new TestCaseData(targetId, featureId).SetName($"{operation}Rejects_{targetId}_{featureId}");
        }
    }

    [TestCaseSource(nameof(RendererRejections))]
    public void RendererRejectsFeature(string target, string feature)
    {
        var dialect = TargetDialect(target);
        foreach (var table in FeatureTables(feature))
        {
            var model = new DubUrl.Schema.Schema([table]);
            Assert.That(() => new SchemaScriptRenderer(dialect).Plan(model), Throws.InstanceOf<NotSupportedException>());
        }
    }

    [TestCaseSource(nameof(InspectionRejections))]
    public void InspectionRejectsFeature(string target, string feature)
    {
        ISchemaMetadataReader reader = target switch
        {
            "Sqlite" => new SqliteMetadataReader(),
            "Oracle" => new OracleMetadataReader(new(23, 26)),
            "SingleStore" => new SingleStoreMetadataReader(),
            "Spark" => new SparkMetadataReader(),
            "Databricks" => new DatabricksMetadataReader(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown schema inspection target."),
        };
        var metadataFeature = feature switch
        {
            "comments" => SchemaFeature.Comments,
            "comparison-check" or "membership-check" or "regex-check" => SchemaFeature.Checks,
            "identity" or "computed" or "stored-computed" or "virtual-computed" => SchemaFeature.Generation,
            "primary-keys" => SchemaFeature.PrimaryKeys,
            "foreign-keys" => SchemaFeature.ForeignKeys,
            "unique" => SchemaFeature.UniqueConstraints,
            "indexes" or "unique-index" or "index-order" => SchemaFeature.Indexes,
            _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown schema inspection feature."),
        };

        Assert.That(reader.Capabilities[metadataFeature], Is.EqualTo(MetadataCoverage.Unsupported));
    }

    private static IEnumerable<Table> FeatureTables(string feature)
    {
        const string table = "coverage_probe";
        var input = new Column("input_value", DbType.Int32);
        var source = new ColumnIdentityExpression(input.Name);
        Table Generated(GeneratedColumnStorage storage) => new(table, [input, new Column(ColumnName, DbType.Int32,
            generation: new GeneratedColumnGeneration(source, storage))]);

        return feature switch
        {
            "computed" => [Generated(GeneratedColumnStorage.Stored), Generated(GeneratedColumnStorage.Virtual)],
            "stored-computed" => [Generated(GeneratedColumnStorage.Stored)],
            "virtual-computed" => [Generated(GeneratedColumnStorage.Virtual)],
            "identity" => [new Table(table, [new Column(ColumnName, DbType.Int64,
                constraints: [new PrimaryKeyConstraint()], generation: new IdentityColumnGeneration(10, 2))])],
            "regex-check" => [new Table(table, [new VarLengthColumn(ColumnName, DbType.String, 40)],
                [new RegexCheckConstraint(new ColumnIdentityExpression(ColumnName), "^a", RegexNullBehavior.RejectNull)])],
            _ => [new Table(table, [FeatureColumn(feature)])],
        };
    }

    private static IDialect TargetDialect(string target) => target switch
    {
        "Sqlite" => Dialect<SqliteDialect>().WithServerVersion(new(3, 50)),
        "DuckDb" => Dialect<DuckDbDialect>(),
        "PostgreSql" => Dialect<PgsqlDialect>().WithServerVersion(new(17, 0)),
        "SqlServer" => Dialect<TSqlDialect>().WithServerVersion(new(16, 0)),
        "SqlServer-2025" => Dialect<TSqlDialect>().WithServerVersion(new(17, 0)),
        "Oracle" => Dialect<OracleDialect>().WithServerVersion(new(23, 26)),
        "SingleStore" => Dialect<SingleStoreDialect>().WithServerVersion(new(9, 1)),
        "MySql" => Dialect<MySqlDialect>().WithInnoDbDefaults(new(8, 4)),
        "Spark" => Dialect<SparkSqlDialect>(),
        "Databricks" => Dialect<DatabricksSqlDialect>().WithTimeType().WithVariantType(),
        "Hive" => Dialect<HiveDialect>(),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown schema QA target."),
    };

    private static Column FeatureColumn(string feature) => feature switch
    {
        "native-array" => Structured(new ArrayType(Integer, true), StructuredStorage.Native),
        "native-map" => Structured(new MapType(Integer, Integer, true), StructuredStorage.Native),
        "native-struct" => Structured(new StructType([new("item", Integer, true)]), StructuredStorage.Native),
        "native-json" => Structured(new JsonType(), StructuredStorage.Json),
        "json-text" => Structured(new JsonType(), StructuredStorage.JsonText),
        "vector" => Structured(new VectorType(DbType.Single, 3), StructuredStorage.Native),
        "current-date" => new(ColumnName, DbType.Date, defaultExpression: ColumnDefaultExpression.CurrentDate),
        "current-time" => new(ColumnName, DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime()),
        "current-timestamp" => new(ColumnName, DbType.DateTime2, defaultExpression: ColumnDefaultExpression.CurrentTimestamp()),
        "utc-timestamp" => new(ColumnName, DbType.DateTime2, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp()),
        "current-user" => TextDefault(ColumnDefaultExpression.CurrentUser),
        "session-user" => TextDefault(ColumnDefaultExpression.SessionUser),
        "current-schema" => TextDefault(ColumnDefaultExpression.CurrentSchema),
        "current-database" => TextDefault(ColumnDefaultExpression.CurrentDatabase),
        "uuid-native" => new(ColumnName, DbType.Guid, defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Native)),
        "uuid-text" => TextDefault(ColumnDefaultExpression.NewUuid(UuidRepresentation.CanonicalText), 36),
        "uuid-binary" => new VarLengthColumn(ColumnName, DbType.Binary, 16,
            defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.Binary, UuidByteOrder.Network)),
        "sequence-default" => new(ColumnName, DbType.Int64,
            defaultExpression: ColumnDefaultExpression.NextSequenceValue(new("coverage_sequence", "qa"))),
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown renderer rejection probe."),
    };

    private static Column Structured(StructuredType shape, StructuredStorage storage)
        => new(ColumnName, new StructuredColumnType(shape, storage), constraints: [new NullableConstraint()]);

    private static Column TextDefault(ColumnDefaultExpression expression, int length = 512)
        => new VarLengthColumn(ColumnName, DbType.String, length, defaultExpression: expression);

    private static T Dialect<T>() where T : IDialect
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<T>(["coverage"]);
        return builder.Build().Get<T>();
    }
}
