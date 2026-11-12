using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Discovery;
using DubUrl.Mapping;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Provider.Spark.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using ForeignKeyConstraint = DubUrl.Schema.Constraints.ForeignKeyConstraint;

namespace DubUrl.Provider.Spark.Tests;

public sealed class SparkSqlDialectTest
{
    [TestCase("odbc+spark", "", typeof(SparkSqlDialect))]
    [TestCase("odbc+spark+http", "", typeof(SparkSqlDialect))]
    [TestCase("odbc+spark+https", "", typeof(SparkSqlDialect))]
    [TestCase("odbc+spark+tcp", "", typeof(SparkSqlDialect))]
    [TestCase("odbc+spark+tcps", "", typeof(SparkSqlDialect))]
    [TestCase("odbc+databricks", "/warehouse/qa", typeof(DatabricksSqlDialect))]
    public void SchemeSelectsDialectEvenWithExplicitDriver(string scheme, string path, Type expected)
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        var registry = new ProviderCatalogBuilder().AddApplicationProviders().Build();
        var url = new ConnectionUrl($"{scheme}://example.com{path}?Driver=Explicit", registry);

        Assert.That(url.Dialect.GetType(), Is.EqualTo(expected));
        Assert.That(DefaultRegistry.GetDialects(scheme).Get(scheme).GetType(), Is.EqualTo(expected));
    }

    [Test]
    public void SparkSessionCatalogPreservesSpellingAndLeavesOmittedQualifiersToSession()
    {
        var dialect = Spark();
        Assert.That(dialect.RenderObjectName("SELECT", []), Is.EqualTo("`SELECT`"));
        Assert.That(dialect.RenderObjectName("SELECT", ["MySchema"]), Is.EqualTo("`MySchema`.`SELECT`"));
        Assert.That(dialect.RenderObjectName("SELECT", ["MySchema"], "spark_catalog"),
            Is.EqualTo("`spark_catalog`.`MySchema`.`SELECT`"));
        Assert.That(() => dialect.RenderObjectName("a.b", []), Throws.ArgumentException);
        Assert.That(() => dialect.RenderObjectName("t", ["one", "two"]), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.RenderObjectName("t", [], "spark_catalog"), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void SparkPluginRulesPreserveLiteralComponentBoundariesAndCatalogCaseSensitivity()
    {
        var dialect = Spark().WithCatalogRules("plugin", new(StringComparer.Ordinal, 2), isCurrent: true);
        Assert.That(dialect.RenderObjectName("table`name", ["outer.ns", "Inner"]),
            Is.EqualTo("`outer.ns`.`Inner`.`table``name`"));
        Assert.That(dialect.RenderObjectName("a.b", ["ns"], "plugin"), Is.EqualTo("`plugin`.`ns`.`a.b`"));
        Assert.That(() => dialect.RenderObjectName("t", ["one", "two", "three"]), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.ValidateObjectNames([new("Name"), new("name")]), Throws.Nothing);
        Assert.That(() => dialect.ValidateObjectNames([new("Name", "default", Catalog: "spark_catalog"), new("name", "default", Catalog: "spark_catalog")]), Throws.ArgumentException);
        Assert.That(() => Spark().ValidateObjectNames([new("Name"), new("name")]), Throws.ArgumentException);
    }

    [Test]
    public void DatabricksValidatesSecurablesAndQuotesLiteralColumns()
    {
        var dialect = Databricks();
        Assert.That(dialect, Is.InstanceOf<SparkSqlDialect>());
        Assert.That(dialect.RenderObjectName("select", []), Is.EqualTo("`select`"));
        Assert.That(dialect.RenderObjectName("qa-table", ["Schema"]), Is.EqualTo("`Schema`.`qa-table`"));
        Assert.That(dialect.RenderObjectName("qa-table", ["Schema"], "Catalog"), Is.EqualTo("`Catalog`.`Schema`.`qa-table`"));
        Assert.That(dialect.SchemaSqlRenderer.RenderIdentifier(dialect, "Col.Name`"), Is.EqualTo("`Col.Name```"));
        Assert.That(() => dialect.RenderObjectName(new string('x', 255), []), Throws.Nothing);
        Assert.That(() => dialect.RenderObjectName(new string('x', 256), []), Throws.ArgumentException);
        Assert.That(() => dialect.RenderObjectName("t", [], "Catalog"), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.RenderObjectName("t", ["s", "nested"]), Throws.TypeOf<NotSupportedException>());
        Assert.That(() => dialect.RenderObjectName("t", ["s"], "bad.catalog"), Throws.ArgumentException);
        Assert.That(() => dialect.RenderObjectName("t", ["bad/schema"]), Throws.ArgumentException);
        Assert.That(() => dialect.ValidateObjectNames([new("table", "Schema", Catalog: "Catalog"),
            new("TABLE", "schema", Catalog: "catalog")]), Throws.ArgumentException);
        Assert.That(() => dialect.ValidateObjectNames([new("table", "one"), new("TABLE", "two")]), Throws.Nothing);
    }

    [TestCase("a.b")]
    [TestCase("a b")]
    [TestCase("a/b")]
    [TestCase("a\u001Fb")]
    [TestCase("a\u007Fb")]
    public void UnityCatalogRejectsForbiddenObjectCharacters(string name)
        => Assert.That(() => Databricks().RenderObjectName(name, []), Throws.ArgumentException);

    [Test]
    public void TablePlanningChecksColumnCollisionsAndUsesSharedRendering()
    {
        var dialect = Databricks();
        var name = new DatabaseObjectName("qa-table", "qa", catalog: "catalog");
        var model = new DubUrl.Schema.Schema([new Table(name, [new Column("Col.Name`", DbType.Int32)])]);
        var sql = new SchemaScriptRenderer(dialect).Plan(model).Single().CommandText;
        Assert.That(sql, Does.Contain("CREATE TABLE `catalog`.`qa`.`qa-table`").And.Contain("`Col.Name```"));

        var context = new TableDefinitionContext(new("t", "qa"), ["Name", "name"]);
        var renderer = (ITableDefinitionSqlRenderer)dialect.SchemaSqlRenderer;
        Assert.That(() => renderer.RenderTableDefinition(dialect, context), Throws.ArgumentException);
        var plugin = Spark().WithCatalogRules("plugin", new(StringComparer.Ordinal), isCurrent: true);
        Assert.That(() => renderer.RenderTableDefinition(plugin, context), Throws.Nothing);
    }

    [Test]
    public void DatabricksRejectsDatabaseQualifier()
    {
        var dialect = Databricks();
        Assert.That(() => dialect.SchemaSqlRenderer.RenderObjectName(dialect, null, "database", "schema", "table"),
            Throws.TypeOf<NotSupportedException>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TablePlanningPreservesFacetsCommentsAndQuotedDelimiters(bool delta)
    {
        var dialect = delta ? Databricks() : Spark();
        var model = new DubUrl.Schema.Schema([new Table("t",
        [
            new VarLengthColumn("a)", DbType.String, 12, description: "Café's \\\\;"),
            new NumericColumn("b", DbType.Decimal, 10, 2),
            new Column("c", DbType.String),
        ], description: "table's comment")]);
        var renderer = new SchemaScriptRenderer(dialect);
        var sql = renderer.Plan(model).Single().CommandText;

        Assert.That(renderer.Render(model), Is.EqualTo(sql.TrimEnd()));
        Assert.That(sql, Does.Contain("`a)` VARCHAR(12) COMMENT 'Café\\'s \\\\\\\\;'"));
        Assert.That(sql, Does.Contain("`b` DECIMAL(10, 2)").And.Contain("`c` STRING"));
        Assert.That(sql, Does.Contain(delta ? ") USING DELTA COMMENT 'table\\'s comment'" : ") USING PARQUET COMMENT 'table\\'s comment'"));
    }

    [Test]
    public void DeltaEnforcesExplicitNotNullAndSparkRejectsIt()
    {
        var model = new DubUrl.Schema.Schema([new Table("t",
            [new Column("id", DbType.Int32, constraints: [new DubUrl.Schema.Constraints.NotNullableConstraint()])])]);
        Assert.That(new SchemaScriptRenderer(Databricks()).Plan(model).Single().CommandText, Does.Contain("`id` INT NOT NULL"));
        Assert.That(() => new SchemaScriptRenderer(Spark()).Plan(model), Throws.Exception.With.Message.Contains("NOT NULL"));
    }

    [TestCase("UUID")]
    [TestCase("HUGEINT")]
    [TestCase("JSON")]
    public void UnsupportedTypeRequiresAnExplicitConversion(string nativeType)
        => Assert.That(() => Databricks().SchemaSqlRenderer.RenderColumnType(Databricks(), nativeType, null, 0, 0, true),
            Throws.TypeOf<NotSupportedException>());


    [TestCase(false)]
    [TestCase(true)]
    public void DefaultsPreserveExplicitNullAndEnableTheDeltaFeatureOnlyWhenNeeded(bool delta)
    {
        var dialect = delta ? Databricks() : Spark();
        var model = new DubUrl.Schema.Schema([new Table("t",
        [
            new Column("without_default", DbType.Int32),
            new Column("with_null", DbType.Int32, DBNull.Value),
            new Column("stamp", DbType.DateTime2, defaultExpression: ColumnDefaultExpression.CurrentUtcTimestamp(6)),
        ])]);
        var renderer = new SchemaScriptRenderer(dialect);
        var sql = renderer.Plan(model).Single().CommandText;
        Assert.That(renderer.Render(model), Is.EqualTo(sql.TrimEnd()));
        Assert.That(sql, Does.Contain("with_null` INT DEFAULT NULL").And.Not.Contain("without_default` INT DEFAULT"));
        Assert.That(sql, Does.Contain("unix_micros(current_timestamp())"));
        Assert.That(sql.Contains("delta.feature.allowColumnDefaults", StringComparison.Ordinal), Is.EqualTo(delta));
    }

    [Test]
    public void DatabricksTimeIsOptInAndNondeterministicUuidDefaultsAreRejected()
    {
        var time = new DubUrl.Schema.Schema([new Table("t",
            [new Column("clock", DbType.Time, defaultExpression: ColumnDefaultExpression.CurrentTime())])]);
        Assert.That(() => new SchemaScriptRenderer(Databricks()).Plan(time), Throws.Exception);
        Assert.That(new SchemaScriptRenderer(Databricks().WithTimeType()).Plan(time).Single().CommandText,
            Does.Contain("TIME DEFAULT current_time()"));
        var uuid = new DubUrl.Schema.Schema([new Table("t",
            [new VarLengthColumn("uuid", DbType.String, 36, defaultExpression: ColumnDefaultExpression.NewUuid(UuidRepresentation.CanonicalText))])]);
        Assert.That(() => new SchemaScriptRenderer(Databricks()).Plan(uuid), Throws.Exception.With.Message.Contains("nondeterministic UUID"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TemporalLiteralsUseTheDeclaredTimestampSemantics(bool delta)
    {
        var local = new DateTime(2024, 3, 10, 2, 30, 0).AddTicks(1_234_560);
        var offset = new DateTimeOffset(local, TimeSpan.FromHours(-8));
        var table = new Table("clock", [
            new Column("local_value", DbType.DateTime2, local),
            new Column("instant_value", DbType.DateTimeOffset, offset),
            new Column("date_value", DbType.Date, new DateOnly(2024, 3, 10)),
        ]);
        var sql = new SchemaScriptRenderer(delta ? Databricks() : Spark()).Render(new([table]));

        Assert.That(sql, Does.Contain("`local_value` TIMESTAMP_NTZ DEFAULT TIMESTAMP_NTZ '2024-03-10 02:30:00.123456'")
            .And.Contain("`instant_value` TIMESTAMP_LTZ DEFAULT TIMESTAMP_LTZ '2024-03-10 02:30:00.123456-08:00'")
            .And.Contain("`date_value` DATE DEFAULT DATE '2024-03-10'"));
    }

    [Test]
    public void DatabricksIdentityRequiresBigintAndPreservesGenerationOptions()
    {
        static DubUrl.Schema.Schema Model(DbType type) => new([new Table("t",
            [new Column("seq", type, generation: new IdentityColumnGeneration(20, -2))])]);
        var renderer = new SchemaScriptRenderer(Databricks(), SchemaCreationOptions.DropIfExists);
        var plan = renderer.Plan(Model(DbType.Int64));
        Assert.That(plan.Select(command => command.Operation), Is.EqualTo(new[] { SchemaSqlOperation.DropTable, SchemaSqlOperation.CreateTable }));
        Assert.That(plan[1].CommandText, Does.Contain("BIGINT GENERATED BY DEFAULT AS IDENTITY (START WITH 20 INCREMENT BY -2)")
            .And.Not.Contain("allowColumnDefaults"));
        Assert.That(() => renderer.Plan(Model(DbType.Int32)), Throws.Exception.With.Message.Contains("BIGINT"));
        Assert.That(() => new SchemaScriptRenderer(Spark()).Plan(Model(DbType.Int64)), Throws.Exception.With.Message.Contains("Delta"));
    }

    [Test]
    public void DeltaChecksAreSeparateCommandsAndPreserveNullAcceptance()
    {
        var value = new ColumnIdentityExpression("score");
        var model = new DubUrl.Schema.Schema([new Table("t",
        [
            new Column("score", DbType.Int32, constraints: [new CheckConstraint(value, ">=", new ValueExpression(0), "score`positive")]),
            new Column("tag", DbType.String),
        ],
        [
            new MembershipCheckConstraint(value, [1, null], NullMembershipBehavior.MatchNull, "allowed_score"),
            new RegexCheckConstraint(new ColumnIdentityExpression("tag"), @"^\d+$", RegexNullBehavior.RejectNull, "numeric_tag"),
        ])]);
        var renderer = new SchemaScriptRenderer(Databricks());
        var plan = renderer.Plan(model);
        Assert.That(plan, Has.Count.EqualTo(4));
        Assert.That(plan[0].CommandText, Does.Contain("USING DELTA").And.Not.Contain("CHECK"));
        Assert.That(plan[1].CommandText, Does.Contain("ADD CONSTRAINT `score``positive` CHECK").And.Contain("coalesce((").And.Contain(">= 0"));
        Assert.That(plan[2].CommandText, Does.Contain("allowed_score").And.Contain("IS NULL"));
        Assert.That(plan[3].CommandText, Does.Contain("numeric_tag").And.Contain("IS NOT NULL").And.Contain("RLIKE"));
        Assert.That(renderer.Render(model),
            Is.EqualTo(string.Join(Environment.NewLine, plan.Select(command => command.CommandText.TrimEnd()))));
    }

    [Test]
    public void DatabricksRejectsDuplicateCheckNamesAcrossColumnAndTableScopes()
    {
        var value = new ColumnIdentityExpression("score");
        var model = new DubUrl.Schema.Schema([new Table("t",
            [new Column("score", DbType.Int32, constraints: [new CheckConstraint(value, ">=", new ValueExpression(0), "ScoreLimit")])],
            [new CheckConstraint(value, "<=", new ValueExpression(100), "scorelimit")])]);

        Assert.That(() => new SchemaScriptRenderer(Databricks()).Plan(model),
            Throws.Exception.With.Message.Contains("Duplicate check constraint name"));
    }

    [Test]
    public void DatabricksRequiresExplicitInformationalAndUniqueCapabilityOptIns()
    {
        var key = new Column("key", DbType.Int32);
        var code = new Column("code", DbType.String);
        var model = new DubUrl.Schema.Schema([new Table("t", [key, code],
            [new PrimaryKeyConstraint(key, "pk"), new UniquenessConstraint([code], "uq")])]);
        Assert.That(() => new SchemaScriptRenderer(Databricks()).Plan(model),
            Throws.Exception.With.Message.Contains("informational"));
        Assert.That(() => new SchemaScriptRenderer(Databricks().WithInformationalKeys()).Plan(model),
            Throws.Exception.With.Message.Contains("UNIQUE"));
        var sql = new SchemaScriptRenderer(Databricks().WithInformationalKeys(supportsUnique: true)).Plan(model).Single().CommandText;
        Assert.That(sql, Does.Contain("PRIMARY KEY (`key`) NOT ENFORCED NORELY")
            .And.Contain("UNIQUE (`code`) NOT ENFORCED NORELY"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DatabricksPreflightsExternalKeysBeforeReplacement(bool supportsUnique)
    {
        var dialect = Databricks().WithInformationalKeys(supportsUnique);
        var parent = new Table(new DatabaseObjectName("Parent's", "external", catalog: "main"),
            [new Column("A", DbType.Int32), new Column("B", DbType.Int32)]);
        var child = new Table("child", [new Column("x", DbType.Int32), new Column("y", DbType.Int32)],
            [new ForeignKeyConstraint("fk", ["y", "x"], parent.Identity, ["B", "A"])]);
        var renderer = new SchemaScriptRenderer(dialect, SchemaCreationOptions.DropIfExists);
        var plan = renderer.Plan(new([child], [], [], [parent]));

        Assert.That(plan.Select(command => command.Operation), Is.EqualTo(new[]
            { SchemaSqlOperation.ForeignKey, SchemaSqlOperation.DropTable, SchemaSqlOperation.CreateTable, SchemaSqlOperation.ForeignKey }));
        Assert.That(plan[0].CommandText, Does.Contain("`main`.information_schema.constraint_column_usage")
            .And.Contain("t.table_name = 'parent\\'s'").And.Contain("IN ('b', 'a')").And.Contain("count(*) = 2"));
        Assert.That(plan[0].CommandText, Does.Contain(supportsUnique
            ? "t.constraint_type IN ('PRIMARY KEY', 'UNIQUE')" : "t.constraint_type IN ('PRIMARY KEY')"));
        Assert.That(plan[1].ObjectName, Is.EqualTo("child"));
        Assert.That(plan[^1].CommandText, Does.Contain("FOREIGN KEY (`y`, `x`) REFERENCES `main`.`external`.`Parent's` (`B`, `A`)")
            .And.Contain("NOT ENFORCED NORELY"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ProjectionViewReplacementUsesDependencyOrder(bool delta)
    {
        var dialect = delta ? Databricks() : Spark();
        var table = new Table("source_table", [new Column("item", DbType.String)]);
        var first = new View("first_view", new ViewQuery(table.Identity,
            new ViewProjection(new FunctionColumnIdentityExpression("Lower", "item"), "folded"),
            new ViewProjection(new ValueExpression("O'Brien;"), "literal")));
        var second = new View("second_view", new ViewQuery(first.Identity,
            new ViewProjection(new ColumnIdentityExpression("folded"))));
        var plan = new SchemaScriptRenderer(dialect, SchemaCreationOptions.DropIfExists)
            .Plan(new DubUrl.Schema.Schema([table], [], [second, first]));
        Assert.That(plan.Select(command => command.ObjectName),
            Is.EqualTo(new[] { "second_view", "first_view", "source_table", "source_table", "first_view", "second_view" }));
        Assert.That(plan[4].CommandText, Does.Contain("LOWER(`item`)").IgnoreCase
            .And.Contain("'O\\'Brien;' AS `literal`"));
    }

    [TestCase("odbc+spark://example.com", typeof(SparkMetadataReader))]
    [TestCase("odbc+databricks://example.com/warehouse/qa", typeof(DatabricksMetadataReader))]
    public void OdbcRoutesRegisterTheirOwnMetadataReader(string address, Type type)
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        var url = new ConnectionUrl(address, new ProviderCatalogBuilder().AddApplicationProviders().Build());
        Assert.That(url.TryGetCapability<SchemaDiscoveryCapability>(out var capability), Is.True);
        Assert.That(capability!.Reader.GetType(), Is.EqualTo(type));
    }

    [Test]
    public void CanonicalDdlDistinguishesExplicitNullAndIdentityModes()
    {
        var columns = SparkCatalogDefinition.Columns("""
            CREATE TABLE `c`.`s`.`t` (
              `without` STRING COMMENT 'DEFAULT NULL',
              `n` INT DEFAULT NULL,
              `id` BIGINT GENERATED BY DEFAULT AS IDENTITY (START WITH 20 INCREMENT BY -2),
              `always` BIGINT GENERATED ALWAYS AS IDENTITY)
            USING DELTA
            """);
        Assert.That(SparkCatalogDefinition.DefaultSql(columns["without"]), Is.Null);
        Assert.That(SparkCatalogDefinition.DefaultSql(columns["n"]), Is.EqualTo("NULL"));
        Assert.That(SparkCatalogDefinition.DefaultSql(columns["id"]), Is.Null);
        Assert.That(SparkCatalogDefinition.Generation(columns["id"], out var supported), Is.EqualTo(new MetadataGeneration(20, -2)));
        Assert.That(supported, Is.False);
        Assert.That(SparkCatalogDefinition.Generation(columns["always"], out var unsupported), Is.Null);
        Assert.That(unsupported, Is.True);
    }

    private static SparkSqlDialect Spark() => DefaultRegistry.GetDialects("odbc+spark").Get<SparkSqlDialect>();
    private static DatabricksSqlDialect Databricks() => DefaultRegistry.GetDialects("odbc+databricks").Get<DatabricksSqlDialect>();
}
