using Index = DubUrl.Schema.Index;
using DubUrl.Schema;
using SchemaModel = DubUrl.Schema.Schema;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using DubUrl.Discovery;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using Constraint = DubUrl.Schema.Constraints.Constraint;
using ForeignKey = DubUrl.Schema.Constraints.ForeignKeyConstraint;

namespace DubUrl.Providers.QA.Contracts.Schema;

/// <summary>Providers supply mandatory observations and explicit expectations;
/// shared scenarios own the assertions.</summary>
[NonParallelizable]
public abstract partial class ProviderContract : SchemaContract
{
    protected abstract IReadOnlyDictionary<string, FeatureExpectation> FeatureExpectations { get; }
    protected abstract void ExecuteScript(DbConnection connection, string script);
    protected abstract string DatabaseErrorCode(DbException exception);
    protected abstract string CurrentSchema(DbConnection connection);
    protected virtual DiscoveryOptions DiscoveryScope(string name) => new() { Schemas = [name] };
    protected abstract DatabaseObjectName QualifiedName(DbConnection connection, string name);
    protected abstract CommentFact ReadComments(DbConnection connection, DatabaseObjectName table);
    protected abstract IReadOnlyList<ColumnFact> ReadColumns(DbConnection connection, DatabaseObjectName table);
    protected abstract IReadOnlyList<IndexFact> ReadIndexes(DbConnection connection, DatabaseObjectName table);
    protected abstract bool ObjectExists(DbConnection connection, DatabaseObjectName name, bool view);
    protected abstract ISchemaMetadataReader? CreateMetadataReader();
    protected abstract PublicDatabase CreatePublicDatabase();
    protected abstract string ExpressionIndexSql(string index, string table, string column);
    protected abstract string FilteredIndexSql(string index, string table, string column);
    protected abstract GeneratedColumnStorage DiscoveryGenerationStorage { get; }
    protected abstract int? ExpectedDiscoveredStringLength { get; }

    protected static string[] ScenarioNames()
    {
        using var stream = typeof(ProviderContract).Assembly.GetManifestResourceStream("SharedScenarios.json")!;
        return JsonSerializer.Deserialize<string[]>(stream)!;
    }
    public static IEnumerable<TestCaseData> SharedScenarios()
        => ScenarioNames().Select(name => new TestCaseData(name).SetName("Shared_" + name));

    private void VerifyDatabaseRejection(DbException error)
    {
        Assert.That(DatabaseErrorCode(error), Is.AnyOf(expectation.ErrorCodes));
        if (expectation.ErrorFragment is not null) Assert.That(error.Message, Does.Contain(expectation.ErrorFragment).IgnoreCase);
    }

    private readonly List<Table> tables = [];
    private readonly List<DatabaseObjectName> views = [];
    private DbConnection database = null!;
    private IDialect dialect = null!;
    private string prefix = "";
    private FeatureExpectation expectation = null!;
    protected virtual string NormalizeTestName(string name) => name;
    private string Name(string suffix) => NormalizeTestName(prefix + suffix);
    private string Q(string name) => dialect.Renderer.Render(name, "identity");
    private string Q(DatabaseObjectName name) => dialect.SchemaSqlRenderer.RenderObjectName(dialect, name.Catalog, name.Database, name.Schema, name.Name);
    private void Execute(string sql) => ExecuteScript(database, sql);
    private object? Scalar(string sql) => QaSql.Rows(database, sql).Single()[0];
    private static Column Id(string name = "id") => new(name, DbType.Int32);
    private static VarLengthColumn Text(string name = "title", string? description = null)
        => new(name, DbType.AnsiString, 40, description: description);
    private SchemaModel Simple(DatabaseObjectName? name = null) => new([new Table(name ?? new(Name("table")), [Id(), Text()])]);

    [TestCaseSource(nameof(SharedScenarios))]
    [Category("SharedProviderContract")]
    public async Task Shared(string scenario)
    {
        Assert.That(FeatureExpectations.Keys, Is.EquivalentTo(ScenarioNames()), "Declare every scenario explicitly.");
        expectation = FeatureExpectations[scenario];
        Assert.That(expectation.Evidence, Is.Not.Empty);
        TestContext.Out.WriteLine($"Expectation: {expectation.Outcome}. {expectation.Evidence}");
        if (expectation.Outcome == ExpectedOutcome.Inapplicable) Assert.Ignore(expectation.Evidence);
        using var connection = CreateConnection();
        database = connection; connection.Open(); dialect = CreateDialect();
        prefix = "Qa" + Guid.NewGuid().ToString("N")[..8] + "_";
        tables.Clear(); views.Clear();
        try
        {
            if (expectation.Outcome == ExpectedOutcome.NoDiscovery)
            {
                Assert.That(scenario.StartsWith("Discovery") || scenario.EndsWith("Diagnostic") || scenario == "DiagnosticDetails", Is.True, "NoDiscovery cannot disable deployment or comment tests.");
                Assert.That(CreateMetadataReader(), Is.Null);
                using var target = CreatePublicDatabase();
                Assert.That(target.Url.TryGetCapability<SchemaDiscoveryCapability>(out _), Is.False);
                var result = await new SchemaInspector().InspectAsync(target.Url);
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.Diagnostics.Select(item => item.Code), Is.EqualTo(new[] { "SDI001" }));
                return;
            }
            if (scenario.StartsWith("Discovery") || scenario.EndsWith("Diagnostic") || scenario == "DiagnosticDetails")
            { await Discovery(scenario); return; }
            RunDeploymentScenario(scenario);
        }
        finally
        {
            Cleanup();
            foreach (var table in tables) Assert.That(ObjectExists(database, table.Identity, false), Is.False, "Leaked table " + table.Name);
            foreach (var view in views) Assert.That(ObjectExists(database, view, true), Is.False, "Leaked view " + view.Name);
        }
    }

    private void RunDeploymentScenario(string scenario)
    {
        Action comments = () => Comments(scenario);
        Action identity = () => Identity(scenario);
        Action generated = () => Generated(scenario);
        Action keys = () => Keys(scenario);
        Action relationships = () => Relationships(scenario);
        Action checks = () => Checks(scenario);
        Action indexes = () => Indexes(scenario);
        Action publicDeployment = () => PublicDeployment(scenario);
        var scenarios = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["ReplaceTable"] = ReplaceTable,
            ["QuotedIdentifiers"] = QuotedIdentifiers,
            ["ColumnTypes"] = ColumnTypes,
            ["ColumnFacets"] = ColumnFacets,
            ["Nullability"] = Nullability,
            ["LiteralDefaults"] = LiteralDefaults,
            ["ProjectionView"] = ProjectionView,
            ["CleanupAfterFailure"] = CleanupAfterFailure,
            ["TableComments"] = comments,
            ["ColumnComments"] = comments,
            ["EmptyComments"] = comments,
            ["CommentsSameBatch"] = comments,
            ["QualifiedComments"] = comments,
            ["UnqualifiedComments"] = comments,
            ["IdentityDefault"] = identity,
            ["IdentitySeedIncrement"] = identity,
            ["GeneratedStored"] = generated,
            ["GeneratedVirtual"] = generated,
            ["SinglePrimaryKey"] = keys,
            ["CompositePrimaryKey"] = keys,
            ["SingleUnique"] = keys,
            ["CompositeUnique"] = keys,
            ["NativeVarcharKey"] = keys,
            ["NativeCharKey"] = keys,
            ["ForeignKey"] = relationships,
            ["CompositeForeignKey"] = relationships,
            ["DependencyCreate"] = relationships,
            ["DependencyDrop"] = relationships,
            ["ComparisonCheck"] = checks,
            ["MembershipCheck"] = checks,
            ["RegexCheck"] = checks,
            ["SingleIndex"] = indexes,
            ["CompositeIndex"] = indexes,
            ["UniqueIndex"] = indexes,
            ["IndexDirection"] = indexes,
            ["PublicDeployment"] = publicDeployment,
            ["FluentDeployment"] = publicDeployment,
        };
        if (scenarios.TryGetValue(scenario, out var run))
            run();
        else
            Assert.Fail("No shared test implemented: " + scenario);
    }

    private void ReplaceTable()
    {
        var schema = Simple();
        if (!Deploy(schema, setup: true)) return;
        Execute($"INSERT INTO {Q(schema.Tables.Values.Single().Identity)} VALUES (1,'old')");
        if (!Deploy(schema, SchemaCreationOptions.DropIfExists)) return;
        Assert.That(Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {Q(schema.Tables.Values.Single().Identity)}")), Is.Zero); return;
    }

    private void QuotedIdentifiers()
    {
        var name = new DatabaseObjectName(Name("select.'雪"));
        if (Deploy(new([new Table(name, [Id("comment_column")])])))
            Assert.That(Convert.ToInt64(Scalar($"SELECT COUNT({Q("comment_column")}) FROM {Q(name)}")), Is.Zero);
        return;
    }

    private void ColumnTypes()
    {
        var table = new Table(Name("types"), [Id(), Text(), new NumericColumn("amount", DbType.Decimal, 10, 2)]);
        if (!Deploy(new([table]))) return;
        Execute($"INSERT INTO {Q(table.Identity)} VALUES (12,'text',12.25)");
        var row = QaSql.Rows(database, $"SELECT {Q("id")},{Q("title")},{Q("amount")} FROM {Q(table.Identity)}").Single();
        Assert.That(Convert.ToInt32(row[0]), Is.EqualTo(12)); Assert.That(row[1], Is.EqualTo("text"));
        Assert.That(Convert.ToDecimal(row[2]), Is.EqualTo(12.25m)); return;
    }

    private void ColumnFacets()
    {
        var table = new Table(Name("facets"), [Text(), new NumericColumn("amount", DbType.Decimal, 10, 2)]);
        if (!Deploy(new([table]))) return;
        var columns = ReadColumns(database, table.Identity);
        Assert.That(columns.Select(c => c.Name), Is.EqualTo(new[] { "title", "amount" }));
        var amount = columns.Single(c => c.Name == "amount");
        Assert.That(amount.Precision, Is.EqualTo(10)); Assert.That(amount.Scale, Is.EqualTo(2));
        if (expectation.Outcome == ExpectedOutcome.NotEnforced)
        {
            Execute($"INSERT INTO {Q(table.Identity)} VALUES ({QaSql.Literal(new string('x', 41))},1)");
            Assert.That(expectation.Evidence, Does.Contain("length"));
        }
        else
        {
            Assert.That(columns.Single(c => c.Name == "title").Length, Is.EqualTo(40));
        }

        return;
    }

    private void Nullability()
    {
        var table = new Table(Name("nullable"), [new Column("required", DbType.Int32, constraints: [new NotNullableConstraint()]), new Column("optional", DbType.Int32, constraints: [new NullableConstraint()])]);
        if (!Deploy(new([table]))) return;
        Execute($"INSERT INTO {Q(table.Identity)} VALUES (1,NULL)");
        Assert.That(QaSql.Rows(database, $"SELECT {Q("optional")} FROM {Q(table.Identity)}").Single()[0], Is.Null);
        Assert.That(() => Execute($"INSERT INTO {Q(table.Identity)} VALUES (NULL,2)"), Throws.InstanceOf<DbException>()); return;
    }

    private void LiteralDefaults()
    {
        var table = new Table(Name("defaults"), [Id(), new NumericColumn("amount", DbType.Decimal, 10, 2, defaultValue: 12.25m), new VarLengthColumn("title", DbType.AnsiString, 40, defaultValue: "Owner's record")]);
        if (!Deploy(new([table]))) return;
        Execute($"INSERT INTO {Q(table.Identity)} ({Q("id")}) VALUES (1)");
        var row = QaSql.Rows(database, $"SELECT {Q("amount")},{Q("title")} FROM {Q(table.Identity)}").Single();
        Assert.That(Convert.ToDecimal(row[0]), Is.EqualTo(12.25m)); Assert.That(row[1], Is.EqualTo("Owner's record")); return;
    }

    private void Identity(string scenario)
    {
        var seeded = scenario == "IdentitySeedIncrement";
        var table = new Table(Name("identity"), [new Column("id", DbType.Int32, constraints: [new PrimaryKeyConstraint()], generation: new IdentityColumnGeneration(seeded ? 10 : 1, seeded ? 2 : 1)), Id("payload")]);
        if (!Deploy(new([table]))) return;
        Execute($"INSERT INTO {Q(table.Identity)} ({Q("payload")}) VALUES (1)");
        Execute($"INSERT INTO {Q(table.Identity)} ({Q("payload")}) VALUES (2)");
        var ids = QaSql.Rows(database, $"SELECT {Q("id")} FROM {Q(table.Identity)} ORDER BY {Q("id")}").Select(r => Convert.ToInt64(r[0])).ToArray();
        Assert.That(ids, Is.EqualTo(seeded ? new long[] { 10, 12 } : new long[] { 1, 2 })); return;
    }

    private void Generated(string scenario)
    {
        var table = new Table(Name("generation"), [Id(), new Column("copy", DbType.Int32, generation: new GeneratedColumnGeneration(new ColumnIdentityExpression("id"), scenario == "GeneratedStored" ? GeneratedColumnStorage.Stored : GeneratedColumnStorage.Virtual))]);
        if (!Deploy(new([table]))) return;
        Execute($"INSERT INTO {Q(table.Identity)} ({Q("id")}) VALUES (7)");
        Assert.That(Convert.ToInt32(Scalar($"SELECT {Q("copy")} FROM {Q(table.Identity)}")), Is.EqualTo(7)); return;
    }

    private void ProjectionView()
    {
        var table = new Table(Name("source"), [Id(), Text()]);
        var view = new View(Name("view"), new ViewQuery(table.Identity, new ViewProjection(new ColumnIdentityExpression("id"), "key"), new ViewProjection(new ColumnIdentityExpression("title"), "label")));
        if (!Deploy(new([table], [], [view]))) return;
        Execute($"INSERT INTO {Q(table.Identity)} VALUES (1,'hello')");
        var row = QaSql.Rows(database, $"SELECT {Q("key")},{Q("label")} FROM {Q(view.Identity)}").Single();
        Assert.That(Convert.ToInt32(row[0]), Is.EqualTo(1)); Assert.That(row[1], Is.EqualTo("hello")); return;
    }

    private void CleanupAfterFailure()
    {
        var schema = Simple(); if (!Deploy(schema)) return;
        Assert.Throws<InvalidOperationException>(() => { try { throw new InvalidOperationException("Deliberate fixture failure"); } finally { Cleanup(); } });
        Assert.That(ObjectExists(database, schema.Tables.Values.Single().Identity, false), Is.False); return;
    }

    protected virtual IReadOnlyList<string> DeploymentCommands(SchemaScriptRenderer renderer, SchemaModel schema)
        => [renderer.Render(schema)];

    private bool Deploy(SchemaModel schema, SchemaCreationOptions options = SchemaCreationOptions.None, bool setup = false)
    {
        Track(schema);
        try
        {
            var commands = DeploymentCommands(new SchemaScriptRenderer(dialect, options), schema);
            if (!setup && expectation.Outcome == ExpectedOutcome.RendererRejects) Assert.Fail("Renderer accepted an explicitly unsupported scenario.");
            foreach (var command in commands) Execute(command);
        }
        catch (NotSupportedException error) when (!setup && expectation.Outcome == ExpectedOutcome.RendererRejects)
        { Assert.That(error.Message, Does.Contain(expectation.ErrorCodes.Single()).IgnoreCase); return false; }
        catch (DbException error) when (!setup && expectation.Outcome == ExpectedOutcome.DatabaseRejects)
        { VerifyDatabaseRejection(error); return false; }
        if (!setup) Assert.That(expectation.Outcome, Is.Not.EqualTo(ExpectedOutcome.DatabaseRejects), "Database accepted an expected rejection.");
        return true;
    }
    private void Track(SchemaModel schema)
    {
        var seen = tables.Select(t => t.Identity).ToHashSet();
        void Visit(Table table)
        {
            if (!seen.Add(table.Identity)) return;
            foreach (var key in table.Constraints.OfType<ForeignKey>())
                if (schema.Tables.TryGetValue(key.TargetTable.Key, out var parent)) Visit(parent);
            tables.Add(table);
        }
        foreach (var table in schema.Tables.Values) Visit(table);
        foreach (var view in schema.Views.Values) if (!views.Contains(view.Identity)) views.Add(view.Identity);
    }
    private void Cleanup()
    {
        foreach (var view in views.AsEnumerable().Reverse()) if (ObjectExists(database, view, true)) Execute("DROP VIEW " + Q(view));
        foreach (var table in tables.AsEnumerable().Reverse()) if (ObjectExists(database, table.Identity, false)) Execute("DROP TABLE " + Q(table.Identity));
    }
    private void Comments(string scenario)
    {
        var identity = scenario == "QualifiedComments" ? QualifiedName(database, Name("comments")) : new DatabaseObjectName(Name("comments"));
        var tableComment = scenario is "ColumnComments" or "EmptyComments" ? null : "Customer's record" + Environment.NewLine + "雪";
        var columnComment = scenario is "TableComments" or "EmptyComments" ? null : "Owner's display name" + Environment.NewLine + "Préféré";
        var table = new Table(identity, [Id(), Text("comment_column", columnComment), Text("none"), Text("empty", ""), Text("blank", " ")], description: tableComment);
        if (!Deploy(new([table]))) return;
        var renderer = new SchemaScriptRenderer(dialect);
        var observed = ReadComments(database, identity);
        if (expectation.Outcome == ExpectedOutcome.NoEngineComments)
        {
            Assert.That(renderer.EngineSupportsComments, Is.False); Assert.That(renderer.SupportsComments, Is.False);
            Assert.That(observed.Table, Is.Null); Assert.That(observed.Columns.Values.All(string.IsNullOrEmpty), Is.True);
        }
        else
        {
            if (scenario != "EmptyComments") Assert.That(renderer.SupportsComments, Is.True);
            Assert.That(observed.Table, Is.EqualTo(expectation.Outcome == ExpectedOutcome.NotEnforced ? null : tableComment));
            Assert.That(observed.Columns.GetValueOrDefault("comment_column"), Is.EqualTo(columnComment));
        }
        foreach (var column in new[] { "none", "empty", "blank" }) Assert.That(observed.Columns.GetValueOrDefault(column), Is.Null.Or.Empty);
    }

    protected virtual TablePhysicalOptions? PhysicalOptions(string scenario) => null;
    protected virtual bool VerifyNativeCharacterKeySemantics => false;

    private void Keys(string scenario)
    {
        var composite = scenario.StartsWith("Composite"); var unique = scenario.EndsWith("Unique");
        var native = scenario.StartsWith("Native") ? new NativeDatabaseType(scenario == "NativeVarcharKey" ? "VARCHAR" : "CHAR") : null;
        Column first = native is null ? Id("b") : new VarLengthColumn("b", DbType.AnsiString, 20, nativeType: native);
        var second = Id("a"); var key = composite ? new[] { first, second } : new[] { first };
        var table = new Table(new DatabaseObjectName(Name("key")), [first, second], unique ? [new UniquenessConstraint(key)] : [new PrimaryKeyConstraint(key)], null, PhysicalOptions(scenario));
        if (!Deploy(new([table]))) return;
        var value = native is null ? "1" : "'STD'";
        Execute($"INSERT INTO {Q(table.Identity)} VALUES ({value},2)");
        var duplicate = $"INSERT INTO {Q(table.Identity)} VALUES ({value},2)";
        if (expectation.Outcome == ExpectedOutcome.Informational)
        {
            Execute(duplicate);
            Assert.That(Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {Q(table.Identity)}")), Is.EqualTo(2));
        }
        else Assert.That(() => Execute(duplicate), Throws.InstanceOf<DbException>());
        if (composite) Execute($"INSERT INTO {Q(table.Identity)} VALUES ({value},3)");
        if (native is not null && VerifyNativeCharacterKeySemantics)
            NativeCharacterKeys(table, native);
    }

    private void NativeCharacterKeys(Table primary, NativeDatabaseType native)
    {
        var code = new VarLengthColumn("code", DbType.AnsiString, 20, nativeType: native);
        var unique = new Table(Name("unique"), [code], [new UniquenessConstraint([code])]);
        var child = new Table(Name("child"), [code],
            [new ForeignKey(Name("fk"), ["code"], unique.Name, ["code"])]);
        Assert.That(Deploy(new([child, unique])), Is.True);

        var primaryName = Q(primary.Identity);
        var uniqueName = Q(unique.Identity);
        var childName = Q(child.Identity);
        Execute($"INSERT INTO {uniqueName} VALUES ('STD')");
        Execute($"INSERT INTO {childName} VALUES ('STD')");
        Assert.That(() => Execute($"INSERT INTO {uniqueName} VALUES ('STD')"), Throws.InstanceOf<DbException>());
        Assert.That(() => Execute($"INSERT INTO {childName} VALUES ('missing')"), Throws.InstanceOf<DbException>());
        Assert.That(() => Execute($"INSERT INTO {primaryName} VALUES ('{new string('x', 21)}', 2)"), Throws.InstanceOf<DbException>());

        if (native.Name.Equals("CHAR", StringComparison.OrdinalIgnoreCase))
        {
            Assert.That(() => Execute($"INSERT INTO {primaryName} VALUES ('STD ', 2)"), Throws.InstanceOf<DbException>());
            Execute($"INSERT INTO {childName} VALUES ('STD ')");
        }
        else
        {
            Execute($"INSERT INTO {primaryName} VALUES ('STD ', 2)");
            Assert.That(() => Execute($"INSERT INTO {childName} VALUES ('STD ')"), Throws.InstanceOf<DbException>());
        }
    }

    private SchemaModel RelationshipSchema(bool composite = true)
    {
        var b = Id("b"); var a = Id("a");
        var parent = new Table(Name("parent"), [b, a], [new PrimaryKeyConstraint(composite ? [b, a] : [b])]);
        var child = new Table(Name("child"), [Id("x"), Id("y")], [new ForeignKey(Name("fk"), composite ? ["x", "y"] : ["x"], parent.Name, composite ? ["b", "a"] : ["b"])]);
        return new([child, parent]);
    }
    private void Relationships(string scenario)
    {
        var schema = RelationshipSchema(scenario != "ForeignKey"); if (!Deploy(schema, setup: false)) return;
        var parent = schema.Tables.Values.Single(t => t.Name == Name("parent"));
        var child = schema.Tables.Values.Single(t => t.Name == Name("child"));
        Execute($"INSERT INTO {Q(parent.Identity)} VALUES (1,2)"); Execute($"INSERT INTO {Q(child.Identity)} VALUES (1,2)");
        if (expectation.Outcome == ExpectedOutcome.Informational)
        {
            Execute($"INSERT INTO {Q(child.Identity)} VALUES (3,4)");
            Assert.That(Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {Q(child.Identity)}")), Is.EqualTo(2));
        }
        else
        {
            Assert.That(() => Execute($"INSERT INTO {Q(child.Identity)} VALUES (3,4)"), Throws.InstanceOf<DbException>());
            if (scenario == "CompositeForeignKey") Assert.That(() => Execute($"INSERT INTO {Q(child.Identity)} VALUES (2,1)"), Throws.InstanceOf<DbException>());
        }
        if (scenario == "DependencyDrop")
        {
            if (!Deploy(schema, SchemaCreationOptions.DropIfExists)) return;
            Assert.That(Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {Q(child.Identity)}")), Is.Zero);
        }
    }
    private void Checks(string scenario)
    {
        Constraint constraint = scenario switch
        {
            "ComparisonCheck" => new CheckConstraint(new ColumnIdentityExpression("value"), ">=", new ValueExpression(0)),
            "MembershipCheck" => new MembershipCheckConstraint(new ColumnIdentityExpression("value"), ["STD", null], NullMembershipBehavior.MatchNull),
            _ => new RegexCheckConstraint(new ColumnIdentityExpression("value"), "^[A-Z]+$", RegexNullBehavior.RejectNull)
        };
        Column column = scenario == "ComparisonCheck" ? new Column("value", DbType.Int32, constraints: [constraint]) : new VarLengthColumn("value", DbType.AnsiString, 40, constraints: [constraint]);
        var table = new Table(Name("check"), [column]); if (!Deploy(new([table]))) return;
        Execute($"INSERT INTO {Q(table.Identity)} VALUES ({(scenario == "ComparisonCheck" ? "0" : "'STD'")})");
        if (scenario == "MembershipCheck") Execute($"INSERT INTO {Q(table.Identity)} VALUES (NULL)");
        var invalid = $"INSERT INTO {Q(table.Identity)} VALUES ({(scenario == "ComparisonCheck" ? "-1" : scenario == "RegexCheck" ? "'123'" : "'invalid'")})";
        if (expectation.Outcome == ExpectedOutcome.NotEnforced)
        {
            Execute(invalid); Assert.That(Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {Q(table.Identity)}")), Is.GreaterThan(1));
        }
        else
        {
            Assert.That(() => Execute(invalid), Throws.InstanceOf<DbException>());
        }

        if (scenario == "RegexCheck" && expectation.Outcome != ExpectedOutcome.NotEnforced)
            Assert.That(() => Execute($"INSERT INTO {Q(table.Identity)} VALUES (NULL)"), Throws.InstanceOf<DbException>());
    }
    private void Indexes(string scenario)
    {
        var table = new Table(new DatabaseObjectName(Name("indexed")), [Id("b"), Id("a")], null, null, PhysicalOptions(scenario)); var composite = scenario is "CompositeIndex" or "IndexDirection";
        var index = new Index(Name("idx"), table.Name, composite ? [new IndexColumn("b", IndexSortDirection.Descending), new IndexColumn("a", IndexSortDirection.Ascending)] : [new IndexColumn("b")], scenario == "UniqueIndex");
        if (!Deploy(new([table], [index]))) return;
        var actual = ReadIndexes(database, table.Identity).Where(i => i.Name == index.Name).OrderBy(i => i.Ordinal).ToArray();
        Assert.That(actual.Select(i => i.Column), Is.EqualTo(composite ? new[] { "b", "a" } : new[] { "b" }));
        Assert.That(actual.All(i => i.Unique == index.IsUnique), Is.True);
        if (scenario == "IndexDirection") Assert.That(actual.Select(i => i.Descending), Is.EqualTo(expectation.Outcome == ExpectedOutcome.NotEnforced ? new[] { false, false } : new[] { true, false }));
        if (index.IsUnique)
        {
            Execute($"INSERT INTO {Q(table.Identity)} VALUES (1,2)");
            Assert.That(() => Execute($"INSERT INTO {Q(table.Identity)} VALUES (1,3)"), Throws.InstanceOf<DbException>());
        }
    }
    private void PublicDeployment(string scenario)
    {
        using var target = CreatePublicDatabase(); var name = Name("public"); var schema = Simple(new(name)); var created = false;
        try
        {
            try
            {
                if (scenario == "FluentDeployment") target.Url.DeploySchema(s => s.WithTables(t => t.Add(x => x.WithName(name).WithColumns(c => c.Add(y => y.WithName("id").WithType(DbType.Int32))))));
                else target.Url.DeploySchema(schema);
                created = true;
            }
            catch (DbException error) when (expectation.Outcome == ExpectedOutcome.DatabaseRejects)
            { VerifyDatabaseRejection(error); return; }
            Assert.That(expectation.Outcome, Is.EqualTo(ExpectedOutcome.Supported));
            using var connection = (DbConnection)target.Url.Open();
            Assert.That(Convert.ToInt64(QaSql.Rows(connection, $"SELECT COUNT(*) FROM {Q(name)}").Single()[0]), Is.Zero);
        }
        finally
        {
            using var connection = (DbConnection)target.Url.Open();
            if (created || ObjectExists(connection, new(name), false)) ExecuteScript(connection, DropTableSql(name));
            Assert.That(ObjectExists(connection, new(name), false), Is.False);
        }
    }
    private async Task<SchemaInspectionResult> Inspect(SchemaFeature[] features, Table[]? selected = null, bool partial = true, bool closure = false)
    {
        var reader = CreateMetadataReader() ?? throw new InvalidOperationException("A supported discovery test requires a real reader.");
        var result = await new SchemaInspector().InspectAsync(database, reader, DiscoveryScope(CurrentSchema(database)) with
        {
            Tables = selected?.Select(t => DiscoveryName(t.Name)).ToArray() ?? [],
            Features = features,
            AllowPartial = partial,
            IncludeReferencedTables = closure
        });
        Assert.That(result.Diagnostics.Select(d => d.Code), Does.Not.Contain("SDI002"), "Catalog access must succeed."); return result;
    }
    private MetadataName DiscoveryName(string name)
    {
        var identity = QualifiedName(database, name);
        return new(identity.Name, identity.Schema, identity.Database, identity.Catalog);
    }
    private async Task Discovery(string scenario)
    {
        var basic = new[] { SchemaFeature.Tables, SchemaFeature.Columns };
        if (scenario is "DiscoveryForeignKey" or "DiscoveryClosure" or "MissingReferenceDiagnostic")
        {
            await DiscoverRelationships(scenario, basic);
            return;
        }
        await DiscoverTable(scenario, basic);
    }

    protected virtual bool DiscoveryIndexIsUnique => true;
    protected virtual IndexSortDirection DiscoveryFirstIndexDirection => IndexSortDirection.Descending;
    protected virtual DbType ExpectedDiscoveredIntegerType => DbType.Int32;
    protected virtual TablePhysicalOptions? DiscoveryPhysicalOptions(string scenario) => null;
    protected virtual Constraint[] DiscoveryUniqueConstraints(Column[] columns)
        => [new UniquenessConstraint([columns[1], columns[0]], Name("unique_pair")),
            new UniquenessConstraint([columns[2]], Name("unique_title"))];

    private async Task DiscoverTable(string scenario, SchemaFeature[] basic)
    {
        var table = CreateDiscoveryTable(scenario);
        var indexes = scenario == "DiscoveryIndexes" ? new[] { new Index(Name("idx"), table.Name, [new IndexColumn("b", DiscoveryFirstIndexDirection), new IndexColumn("a", IndexSortDirection.Ascending)], DiscoveryIndexIsUnique) } : [];
        var portable = new View(Name("portable"), new ViewQuery(table.Identity, new ViewProjection(new ColumnIdentityExpression("b"), "label")));
        if (!DeployDiscoveryTable(scenario, table, indexes, portable)) return;
        if (scenario is "ExpressionIndexDiagnostic" or "FilteredIndexDiagnostic")
        {
            await DiscoverUnsupportedIndex(scenario, table, basic);
            return;
        }
        if (scenario is "DiscoveryPartial" or "DiscoveryStrict" or "AggregateViewDiagnostic" or "DiagnosticDetails")
        {
            await DiscoverUnsupportedView(scenario, table, basic);
            return;
        }
        await DiscoverPortableTable(scenario, table, indexes, portable, basic);
    }

    private bool DeployDiscoveryTable(string scenario, Table table, Index[] indexes, View portable)
        => Deploy(new([table], indexes, scenario is "DiscoveryViews" or "AggregateViewDiagnostic" ? [portable] : []),
            setup: scenario is "ExpressionIndexDiagnostic" or "FilteredIndexDiagnostic" or "DiscoveryRoundTrip");

    private async Task DiscoverPortableTable(string scenario, Table table, Index[] indexes, View portable, SchemaFeature[] basic)
    {
        var feature = DiscoveryFeature(scenario);
        if (scenario == "DiscoveryTableFilter") Deploy(new([new Table(Name("unrelated"), [Id()])]));
        var informational = expectation.Outcome == ExpectedOutcome.Informational;
        SchemaFeature[] features = scenario == "DiscoveryUnique" && !informational ? [.. basic, feature, SchemaFeature.Indexes] : [.. basic, feature];
        var inspected = await Inspect(features, scenario is "DiscoveryViews" or "DiscoverySchemaFilter" ? null : [table], partial: scenario != "DiscoveryUnique" || informational);
        Assert.That(inspected.IsSuccess, Is.True, string.Join(",", inspected.Diagnostics.Select(d => d.Code)));
        if (expectation.Outcome == ExpectedOutcome.NotEnforced && scenario != "DiscoveryIndexes")
        {
            VerifyUnsupportedDiscovery(scenario, inspected, feature);
            return;
        }
        if (informational)
        {
            Assert.That(inspected.IsComplete, Is.False);
            Assert.That(inspected.Diagnostics.Any(item => item.Feature == feature && item.Coverage == MetadataCoverage.Unrepresentable), Is.True);
        }
        await VerifyDiscoveredSchema(scenario, table, indexes, portable, inspected, basic);
    }

    private static void VerifyUnsupportedDiscovery(string scenario, SchemaInspectionResult inspected, SchemaFeature feature)
    {
        Assert.That(inspected.IsComplete, Is.False);
        Assert.That(inspected.Diagnostics.Any(d => d.Feature == feature && d.Coverage is MetadataCoverage.Unsupported or MetadataCoverage.Unrepresentable), Is.True);
        var diagnosticCode = scenario == "DiscoveryGeneration" ? "SDI009" : "SDI003";
        Assert.That(inspected.Diagnostics.Where(d => d.Feature == feature).Select(d => d.Code), Does.Contain(diagnosticCode));
    }

    private async Task DiscoverRelationships(string scenario, SchemaFeature[] basic)
    {
        var schema = RelationshipSchema(); if (!Deploy(schema)) return;
        var child = schema.Tables.Values.Single(t => t.Name == Name("child"));
        var result = await Inspect([.. basic, SchemaFeature.PrimaryKeys, SchemaFeature.ForeignKeys], scenario == "DiscoveryForeignKey" ? schema.Tables.Values.ToArray() : [child], closure: scenario == "DiscoveryClosure");
        if (scenario == "MissingReferenceDiagnostic")
        {
            Assert.That(result.Diagnostics.Where(d => d.Code == "SDI005").Select(d => d.Feature), Is.EqualTo(new[] { SchemaFeature.ForeignKeys }));
            Assert.That(result.IsComplete, Is.False); return;
        }
        Assert.That(result.IsComplete, Is.EqualTo(expectation.Outcome != ExpectedOutcome.Informational), string.Join(",", result.Diagnostics.Select(d => d.Code)));
        Assert.That(result.Schema!.Tables.Count, Is.EqualTo(2));
        var key = result.Schema.Tables.Values.Single(t => t.Name == child.Name).Constraints.OfType<ForeignKey>().Single();
        Assert.That(key.SourceColumns, Is.EqualTo(new[] { "x", "y" })); Assert.That(key.TargetColumns, Is.EqualTo(new[] { "b", "a" })); return;
    }

    private Table CreateDiscoveryTable(string scenario)
    {
        var a = Id("b"); var b = Id("a");
        var columns = new Column[] { a, b, Text(), new NumericColumn("amount", DbType.Decimal, 10, 2) }; Constraint[] constraints = [];
        if (scenario == "DiscoveryFacets") columns = [new Column("b", DbType.Int32, constraints: [new NotNullableConstraint()]), Text(), new NumericColumn("amount", DbType.Decimal, 10, 2)];
        if (scenario == "DiscoveryPrimaryKey") constraints = [new PrimaryKeyConstraint([a, b])];
        if (scenario == "DiscoveryUnique") constraints = DiscoveryUniqueConstraints(columns);
        if (scenario == "DiscoveryDefaults") columns = [a, new VarLengthColumn("title", DbType.AnsiString, 40, defaultValue: "hé"), new NumericColumn("amount", DbType.Decimal, 10, 2, defaultValue: 12.25m)];
        if (scenario == "DiscoveryIdentity") columns = [new Column("id", DbType.Int32, constraints: [new PrimaryKeyConstraint()], generation: new IdentityColumnGeneration(10, 2)), Id("payload")];
        if (scenario == "DiscoveryChecks") constraints = [new CheckConstraint(new ColumnIdentityExpression("b"), ">=", new ValueExpression(0))];
        if (scenario == "DiscoveryGeneration") columns = [a, new Column("copy", DbType.Int32, generation: new GeneratedColumnGeneration(new ColumnIdentityExpression("b"), DiscoveryGenerationStorage))];
        if (scenario == "DiscoveryComments") columns = [Id(), Text("title", "column comment")];
        var table = new Table(new DatabaseObjectName(Name("discovery")), columns, constraints, scenario == "DiscoveryComments" ? "table comment" : null, DiscoveryPhysicalOptions(scenario));
        return table;
    }

    private async Task DiscoverUnsupportedIndex(string scenario, Table table, SchemaFeature[] basic)
    {
        try
        {
            Execute(scenario == "ExpressionIndexDiagnostic" ? ExpressionIndexSql(Q(Name("expr")), Q(table.Identity), Q("title")) : FilteredIndexSql(Q(Name("partial")), Q(table.Identity), Q("b")));
        }
        catch (DbException error) when (expectation.Outcome == ExpectedOutcome.DatabaseRejects)
        { VerifyDatabaseRejection(error); return; }
        Assert.That(expectation.Outcome, Is.Not.EqualTo(ExpectedOutcome.DatabaseRejects));
        var result = await Inspect([.. basic, SchemaFeature.Indexes], [table]);
        var diagnostics = result.Diagnostics.Where(d => d.Feature == SchemaFeature.Indexes).ToArray();
        Assert.That(diagnostics.Length, Is.EqualTo(1)); Assert.That(diagnostics[0].Code, Is.EqualTo("SDI009"));
        Assert.That(diagnostics[0].Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable)); Assert.That(result.Schema!.Indexes, Is.Empty); return;
    }

    private async Task DiscoverUnsupportedView(string scenario, Table table, SchemaFeature[] basic)
    {
        var aggregate = new DatabaseObjectName(Name("aggregate")); views.Add(aggregate);
        Execute($"CREATE VIEW {Q(aggregate)} AS SELECT COUNT(*) AS {Q("total")} FROM {Q(table.Identity)}");
        var result = await Inspect([.. basic, SchemaFeature.Views], partial: scenario != "DiscoveryStrict");
        Assert.That(result.IsSuccess, Is.EqualTo(scenario != "DiscoveryStrict")); Assert.That(result.IsComplete, Is.False);
        var diagnostics = result.Diagnostics.Where(d => d.Object?.Name == aggregate.Name).ToArray();
        Assert.That(diagnostics.Length, Is.EqualTo(1)); Assert.That(diagnostics[0].Code, Is.EqualTo("SDI008"));
        Assert.That(diagnostics[0].Feature, Is.EqualTo(SchemaFeature.Views));
        Assert.That(diagnostics[0].Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(result.Schema!.Views.Values.Select(v => v.Name), Does.Not.Contain(aggregate.Name)); return;
    }

    private static SchemaFeature DiscoveryFeature(string scenario)
        => scenario switch
        {
            "DiscoveryPrimaryKey" => SchemaFeature.PrimaryKeys,
            "DiscoveryUnique" => SchemaFeature.UniqueConstraints,
            "DiscoveryDefaults" => SchemaFeature.Defaults,
            "DiscoveryGeneration" or "DiscoveryIdentity" => SchemaFeature.Generation,
            "DiscoveryChecks" => SchemaFeature.Checks,
            "DiscoveryComments" => SchemaFeature.Comments,
            "DiscoveryIndexes" => SchemaFeature.Indexes,
            "DiscoveryViews" => SchemaFeature.Views,
            _ => SchemaFeature.Columns
        };

    private async Task VerifyDiscoveredSchema(string scenario, Table table, Index[] indexes, View portable, SchemaInspectionResult inspected, SchemaFeature[] basic)
    {
        var actual = inspected.Schema!.Tables.Values.Single(t => t.Name == table.Name);
        if (scenario == "DiscoveryUnique" && expectation.Outcome != ExpectedOutcome.Informational)
        {
            Assert.That(inspected.IsComplete, Is.True);
            Assert.That(inspected.Coverage[SchemaFeature.UniqueConstraints], Is.EqualTo(MetadataCoverage.Available));
            Assert.That(inspected.Diagnostics, Is.Empty);
            Assert.That(inspected.Schema.Indexes, Is.Empty);
            var metadata = await CreateMetadataReader()!.ReadAsync(database, DiscoveryScope(CurrentSchema(database)) with
            {
                Tables = [DiscoveryName(table.Name)],
                Features = [.. basic, SchemaFeature.UniqueConstraints]
            }, CancellationToken.None);
            var keys = metadata.Tables.Single(t => t.Identity.Name == table.Name).UniqueConstraints;
            // Some catalogs expose generated key names rather than the declared constraint names.
            foreach (var key in keys)
                Assert.That(actual.Constraints.OfType<UniquenessConstraint>().Single(c => c.Name == key.Name).Columns.Select(c => c.Name), Is.EqualTo(key.Columns));
        }
        if (VerifyDiscoveredColumns(scenario, actual, table)) return;
        switch (scenario)
        {
            case "DiscoveryIndexes":
                var ix = inspected.Schema.Indexes.Values.Single(i => i.Name == indexes[0].Name);
                Assert.That(ix.IsUnique, Is.EqualTo(indexes[0].IsUnique)); Assert.That(ix.Columns.Keys, Is.EqualTo(new[] { "b", "a" }));
                Assert.That(ix.Columns.Values.Select(c => c.SortDirection), Is.EqualTo(expectation.Outcome == ExpectedOutcome.NotEnforced ? new IndexSortDirection?[] { null, null } : new IndexSortDirection?[] { DiscoveryFirstIndexDirection, IndexSortDirection.Ascending })); break;
            case "DiscoveryViews": Assert.That(inspected.Schema.Views.Values.Single(v => v.Name == portable.Name).Query.Projections.Single().Alias, Is.EqualTo("label")); break;
            case "DiscoveryTableFilter": Assert.That(inspected.Schema.Tables.Values.Select(t => t.Name), Is.EqualTo(new[] { table.Name })); break;
            case "DiscoverySchemaFilter":
                Assert.That(inspected.Schema.Tables.Values.All(t => t.Identity == QualifiedName(database, t.Name)), Is.True);
                var excluded = await new SchemaInspector().InspectAsync(database, CreateMetadataReader()!, DiscoveryScope(Name("absent")) with { Features = basic });
                Assert.That(excluded.Schema!.Tables, Is.Empty); break;
            case "DiscoveryRoundTrip":
                Execute(DropTableSql(table.Name)); if (!Deploy(new(inspected.Schema.Tables.Values.ToArray()))) return;
                var second = await Inspect(basic, [table]); var again = second.Schema!.Tables.Values.Single();
                Assert.That(again.Identity, Is.EqualTo(actual.Identity));
                Assert.That(again.Columns.Values.Select(c => (c.Name, c.Type)), Is.EqualTo(actual.Columns.Values.Select(c => (c.Name, c.Type)))); break;
            default: Assert.Fail("No discovery assertion: " + scenario); break;
        }
    }

    private bool VerifyDiscoveredColumns(string scenario, Table actual, Table source)
    {
        switch (scenario)
        {
            case "DiscoveryTablesColumns": Assert.That(actual.Columns.Keys, Is.EqualTo(source.Columns.Values.Select(c => c.Name))); break;
            case "DiscoveryFacets":
                Assert.That(actual.Columns["b"].Type, Is.EqualTo(ExpectedDiscoveredIntegerType));
                Assert.That(actual.Columns["b"].Constraints.OfType<NotNullableConstraint>(), Has.Exactly(1).Items);
                Assert.That(actual.Columns["title"].Constraints.OfType<NullableConstraint>(), Has.Exactly(1).Items);
                if (ExpectedDiscoveredStringLength is int length)
                    Assert.That(((VarLengthColumn)actual.Columns["title"]).Length, Is.EqualTo(length));
                else Assert.That(actual.Columns["title"], Is.Not.TypeOf<VarLengthColumn>());
                Assert.That(actual.Columns["amount"], Is.TypeOf<NumericColumn>());
                Assert.That(((NumericColumn)actual.Columns["amount"]).Precision, Is.EqualTo(10)); Assert.That(((NumericColumn)actual.Columns["amount"]).Scale, Is.EqualTo(2)); break;
            case "DiscoveryPrimaryKey": Assert.That(actual.Constraints.OfType<PrimaryKeyConstraint>().Single().Columns.Keys, Is.EqualTo(new[] { "b", "a" })); break;
            case "DiscoveryUnique":
                var unique = actual.Constraints.OfType<UniquenessConstraint>().ToArray();
                Assert.That(unique, Has.Length.EqualTo(2));
                foreach (var expected in source.Constraints.OfType<UniquenessConstraint>())
                    Assert.That(unique.Count(c => c.Columns.Select(column => column.Name).SequenceEqual(expected.Columns.Select(column => column.Name))), Is.EqualTo(1));
                break;
            case "DiscoveryDefaults": Assert.That(actual.Columns["title"].DefaultValue, Is.EqualTo("hé")); Assert.That(actual.Columns["amount"].DefaultValue, Is.EqualTo(12.25m)); break;
            case "DiscoveryIdentity":
                var identity = (IdentityColumnGeneration)actual.Columns["id"].Generation!;
                Assert.That(identity.Seed, Is.EqualTo(10)); Assert.That(identity.Increment, Is.EqualTo(2)); break;
            case "DiscoveryChecks":
                var check = actual.Constraints.OfType<CheckConstraint>().Single();
                Assert.That(check.Left, Is.TypeOf<ColumnIdentityExpression>()); Assert.That(check.Operator, Is.EqualTo(">=")); Assert.That(((ValueExpression)check.Right).Value, Is.EqualTo(0)); break;
            case "DiscoveryGeneration": Assert.That(actual.Columns["copy"].Generation, Is.TypeOf<GeneratedColumnGeneration>()); break;
            case "DiscoveryComments": Assert.That(actual.Description, Is.EqualTo("table comment")); Assert.That(actual.Columns["title"].Description, Is.EqualTo("column comment")); break;
            default: return false;
        }
        return true;
    }
}
