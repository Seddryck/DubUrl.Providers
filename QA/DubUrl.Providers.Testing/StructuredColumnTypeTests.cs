using System.Data;
using DubUrl.Discovery;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Provider.MySql.Schema.Generation;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Provider.Oracle.Discovery;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Provider.PostgreSql.Sql;
using DubUrl.Provider.SqlServer.Discovery;
using DubUrl.Provider.SqlServer.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public class StructuredColumnTypeTests
{
    private static readonly ScalarType Integer = new(DbType.Int32);

    [Test]
    public void JsonUsesTheExplicitRepresentation()
    {
        var shape = new JsonType(new StructType([new("items", new ArrayType(Integer, true), false)]));
        Assert.That(Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Json)), Does.Contain(" JSON"));
        Assert.That(Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.JsonText)), Does.Contain(" VARCHAR"));
        Assert.That(Plan(Dialect<PgsqlDialect>(), new(shape, StructuredStorage.Json)), Does.Contain(" JSONB"));
        Assert.That(Plan(Dialect<MySqlDialect>().WithInnoDbDefaults(new(8, 4)), new(shape, StructuredStorage.Json)), Does.Contain(" JSON"));
        Assert.That(() => Plan(Dialect<MySqlDialect>().WithMariaDbDefaults(new(11, 4)), new(shape, StructuredStorage.Json)),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Binary)),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void DuckDbQuotesFieldNamesAndPreservesNesting()
    {
        var fieldName = "select\"), DROP TABLE t";
        var shape = new StructType([new(fieldName, new MapType(Integer, new ArrayType(Integer, true), true), true)]);
        var sqlType = """STRUCT("select""), DROP TABLE t" MAP(INTEGER, INTEGER[]))""";
        Assert.That(Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Native)), Does.Contain(sqlType));
        Assert.That(DuckDbCatalogSql.StructuredType(sqlType)?.Type?.Shape, Is.EqualTo(shape));
    }

    private static IEnumerable<StructuredType> InvalidDuckDbShapes()
    {
        yield return new ArrayType(new ArrayType(Integer, false), true);
        yield return new ArrayType(Integer, null);
        yield return new MapType(new ArrayType(Integer, true), Integer, true);
        yield return new MapType(Integer, Integer, false);
        yield return new StructType([new("Key", Integer, true), new("key", Integer, true)]);
        yield return new StructType([new("field", Integer, false)]);
        yield return new ArrayType(new ScalarType(DbType.Decimal), true);
    }

    [TestCaseSource(nameof(InvalidDuckDbShapes))]
    public void DuckDbRejectsUnsupportedNestedDefinitions(StructuredType shape)
        => Assert.That(() => Plan(Dialect<DuckDbDialect>(), new(shape, StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());

    [TestCase("FLOAT[3]")]
    [TestCase("DECIMAL(6,2)[]")]
    [TestCase("STRUCT(a INTEGER, A VARCHAR)")]
    [TestCase("MAP(INTEGER[], INTEGER)")]
    [TestCase("UNION(a INTEGER, b VARCHAR)")]
    public void DuckDbReportsUnrepresentableCatalogTypes(string name)
    {
        var metadata = DuckDbCatalogSql.StructuredType(name);
        Assert.That(metadata?.Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(metadata?.Type, Is.Null);
    }

    [Test]
    public void SingleStoreValidatesDimensionsAndComponentTypes()
    {
        var dialect = Dialect<SingleStoreDialect>().WithServerVersion(new(9, 1));
        Assert.That(Plan(dialect, new(new VectorType(DbType.Single, 3), StructuredStorage.Native)),
            Does.Contain("VECTOR(3, F32)"));
        Assert.That(SingleStoreVectorType.Decode("vector(3, F32)").Type?.Shape, Is.EqualTo(new VectorType(DbType.Single, 3)));
        Assert.That(SingleStoreVectorType.Decode("vector(3, F16)").Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(() => new VectorType(DbType.Single, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => Plan(dialect, new(new VectorType(DbType.Single, int.MaxValue), StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(dialect, new(new VectorType(DbType.Decimal, 3), StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void SqlServerQualifiesNativeTypesByVersion()
    {
        var dialect = Dialect<TSqlDialect>();
        var sql2022 = dialect.WithServerVersion(new(16, 0));
        var sql2025 = dialect.WithServerVersion(new(17, 0));
        var json = new StructuredColumnType(new JsonType(), StructuredStorage.Json);
        var vector = new StructuredColumnType(new VectorType(DbType.Single, 1998), StructuredStorage.Native);
        Assert.That(Plan(dialect, new(new JsonType(), StructuredStorage.JsonText)), Does.Contain("NVARCHAR(MAX)"));
        Assert.That(Plan(sql2025, json), Does.Contain(" JSON"));
        Assert.That(Plan(sql2025, vector), Does.Contain("VECTOR(1998)"));
        Assert.That(() => Plan(sql2022, json), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(dialect, vector), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(sql2025, new(new VectorType(DbType.Single, 1999), StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(sql2025, new(new VectorType(DbType.Double, 3), StructuredStorage.Native)),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(sql2025, vector, defaultValue: DBNull.Value, constraints: [new NullableConstraint()]),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(sql2025, vector, constraints: [new CheckConstraint(new ColumnIdentityExpression("v"), "=", new ValueExpression(0))]),
            Throws.TypeOf<NotSupportedException>());
    }

    [TestCase(0, 3, MetadataCoverage.Available)]
    [TestCase(1, 3, MetadataCoverage.Unrepresentable)]
    [TestCase(0, null, MetadataCoverage.Unrepresentable)]
    public void SqlServerDiscoveryRequiresRepresentableVectorMetadata(int baseType, int? dimensions, MetadataCoverage coverage)
    {
        var row = new CatalogRow(new Dictionary<string, object?>
        {
            ["type_name"] = "vector", ["user_defined"] = false,
            ["vector_base_type"] = baseType, ["vector_dimensions"] = dimensions
        });
        var result = SqlServerMetadataReader.DecodeStructuredType(row);
        Assert.That(result?.Coverage, Is.EqualTo(coverage));
        Assert.That(result?.Type?.Shape, Is.EqualTo(coverage == MetadataCoverage.Available ? new VectorType(DbType.Single, 3) : null));
        Assert.That(SqlServerMetadataReader.DecodeStructuredType(row.With(("user_defined", true))), Is.Null);
    }

    [TestCase(DbType.SByte, "INT8")]
    [TestCase(DbType.Single, "FLOAT32")]
    [TestCase(DbType.Double, "FLOAT64")]
    public void OracleQualifiesFixedVectorsByVersionAndCompatibility(DbType elementType, string format)
    {
        var dialect = Dialect<OracleDialect>();
        var vector = new StructuredColumnType(new VectorType(elementType, 65535), StructuredStorage.Native);
        Assert.That(Plan(dialect.WithServerVersion(new(23, 4)), vector), Does.Contain($"VECTOR(65535, {format})"));
        Assert.That(() => Plan(dialect.WithServerVersion(new(23, 26), new(23, 3)), vector), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(dialect.WithServerVersion(new(21, 0)), vector), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void OracleQualifiesNativeJsonAndRejectsUnsupportedVectorDefinitions()
    {
        var dialect = Dialect<OracleDialect>();
        var json = new StructuredColumnType(new JsonType(), StructuredStorage.Json);
        Assert.That(Plan(dialect.WithServerVersion(new(21, 0), new(20, 0)), json), Does.Contain(" JSON"));
        Assert.That(() => Plan(dialect, json), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(dialect.WithServerVersion(new(19, 0)), json), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(dialect.WithServerVersion(new(23, 26), new(19, 0)), json), Throws.TypeOf<NotSupportedException>());

        var recent = dialect.WithServerVersion(new(23, 26));
        var vector = new StructuredColumnType(new VectorType(DbType.Single, 3), StructuredStorage.Native);
        Assert.That(() => Plan(recent, new(new VectorType(DbType.Single, 65536), StructuredStorage.Native)), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(recent, new(new VectorType(DbType.Byte, 3), StructuredStorage.Native)), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Plan(recent, vector, defaultValue: DBNull.Value, constraints: [new NullableConstraint()]),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void OracleReportsMissingVectorDetailsWithoutGuessing()
    {
        var row = new CatalogRow(new Dictionary<string, object?> { ["type_name"] = "VECTOR" });
        Assert.That(OracleMetadataReader.DecodeStructuredType(row),
            Is.EqualTo(new StructuredColumnMetadata(null, MetadataCoverage.Unrepresentable)));
        Assert.That(OracleMetadataReader.DecodeStructuredType(row.With(("structured_inaccessible", true))),
            Is.EqualTo(new StructuredColumnMetadata(null, MetadataCoverage.Inaccessible)));
        Assert.That(OracleMetadataReader.DecodeStructuredType(row.With(("type_owner", "USER_SCHEMA"))), Is.Null);
    }

    private static string Plan(IDialect dialect, StructuredColumnType type, object? defaultValue = null, IConstraint[]? constraints = null)
        => string.Join("\n", new SchemaScriptRenderer(dialect).Plan(new(
            [new Table("structured_values", [new Column("v", type, defaultValue, constraints)], [])])).Select(command => command.CommandText));

    private static TDialect Dialect<TDialect>() where TDialect : IDialect
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<TDialect>(["test"]);
        return builder.Build().Get<TDialect>();
    }
}
