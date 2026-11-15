using System.Data.Common;
using ConnectionState = System.Data.ConnectionState;
using DubUrl.Discovery;
using DubUrl.Provider.Oracle.Discovery;
using DubUrl.Provider.Oracle.QA;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Providers.QA.Contracts.Schema;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using NUnit.Framework;
using Oracle.ManagedDataAccess.Client;

namespace DubUrl.Schema.Oracle.QA;

[TestFixture]
[Category("Oracle")]
[Category("Schema")]
[Category("Discovery")]
public sealed class OracleDiscoveryTests
{
    private static readonly SchemaFeature[] KeyFeatures =
        [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.PrimaryKeys, SchemaFeature.UniqueConstraints,
            SchemaFeature.ForeignKeys, SchemaFeature.Indexes];

    [Test]
    public async Task NativeUniqueRoundTripPreservesPartiallyNullTupleBehavior()
    {
        using var schema = OracleTestDatabase.CreateSchema();
        using var connection = schema.Connect();
        connection.Open();
        var identity = new DatabaseObjectName("unique.items", schema.Name);
        var dialect = Dialect();
        var renderer = new SchemaScriptRenderer(dialect);
        var tableSql = UniqueNullPolicyChecks.SqlName(dialect, identity);
        QaSql.Execute(connection, $"CREATE TABLE {tableSql} (A NUMBER(12,0), B NUMBER, LABEL VARCHAR2(20 CHAR))");
        QaSql.Execute(connection, $"CREATE UNIQUE INDEX UQ_BACKING ON {tableSql} (B,A)");
        QaSql.Execute(connection, $"ALTER TABLE {tableSql} ADD CONSTRAINT UQ_PAIR UNIQUE (B,A) USING INDEX UQ_BACKING");
        QaSql.Execute(connection, $"ALTER TABLE {tableSql} ADD CONSTRAINT UQ_LABEL UNIQUE (LABEL)");

        var options = new DiscoveryOptions { Tables = [new(identity.Name, identity.Schema)], Features = KeyFeatures };
        var inspector = new SchemaInspector();
        DbProviderFactories.RegisterFactory("Oracle.ManagedDataAccess", OracleClientFactory.Instance);
        using var target = QaSql.Public("oracle", OracleTestDatabase.Connect());
        Assert.That(target.Url.TryGetCapability<SchemaDiscoveryCapability>(out var capability), Is.True);
        Assert.That(capability!.Reader, Is.TypeOf<OracleMetadataReader>());
        var first = await inspector.InspectAsync(target.Url, options);
        Verify(first);
        VerifyTuples();

        var commands = renderer.Plan(first.Schema!);
        Assert.That(commands.Select(command => command.Operation), Is.EqualTo(new[] { SchemaSqlOperation.CreateTable }));
        QaSql.Execute(connection, $"DROP TABLE {tableSql} PURGE");
        QaSql.Execute(connection, commands.Single().CommandText);

        var second = await inspector.InspectAsync(connection, new OracleMetadataReader(), options);
        Verify(second);
        VerifyTuples();
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));

        void Verify(SchemaInspectionResult result)
        {
            AssertComplete(result);
            var table = result.Schema!.Tables.Values.Single();
            Assert.Multiple(() =>
            {
                Assert.That(table.Identity, Is.EqualTo(identity));
                Assert.That(table.Columns.Keys, Is.EqualTo(new[] { "A", "B", "LABEL" }));
                Assert.That(((NumericColumn)table.Columns["A"]).Precision, Is.EqualTo(12));
                Assert.That(table.Columns["B"], Is.Not.InstanceOf<NumericColumn>());
                Assert.That(((VarLengthColumn)table.Columns["LABEL"]).Length, Is.EqualTo(20));
                Assert.That(table.Columns["LABEL"].NativeType!.Name, Is.EqualTo("VARCHAR2 CHAR"));
                Assert.That(result.Schema.Indexes, Is.Empty);
                Assert.That(result.Coverage[SchemaFeature.UniqueConstraints], Is.EqualTo(MetadataCoverage.Available));
            });
            var keys = table.Constraints.OfType<UniquenessConstraint>().ToDictionary(key => key.Name!);
            Assert.That(keys.Keys, Is.EquivalentTo(new[] { "UQ_PAIR", "UQ_LABEL" }));
            Assert.That(keys["UQ_PAIR"].Columns.Select(column => column.Name), Is.EqualTo(new[] { "B", "A" }));
            Assert.That(keys["UQ_PAIR"].NullPolicy, Is.EqualTo(UniqueNullPolicy.Native));
            Assert.That(keys["UQ_LABEL"].Columns.Select(column => column.Name), Is.EqualTo(new[] { "LABEL" }));
            Assert.That(keys["UQ_LABEL"].NullPolicy, Is.EqualTo(UniqueNullPolicy.NullsDistinct));
        }

        void VerifyTuples()
        {
            // The projection allows the shared two-column tuple checks to omit LABEL.
            UniqueNullPolicyChecks.VerifyTuples(connection, $"(SELECT A, B FROM {tableSql})",
                error => error is OracleException { Number: 1 },
                allowsPartialNullDuplicates: false, allowsAllNullDuplicates: true);
            QaSql.Execute(connection, $"INSERT INTO {tableSql} (LABEL) VALUES ('unique label')");
            Assert.That(() => QaSql.Execute(connection, $"INSERT INTO {tableSql} (LABEL) VALUES ('unique label')"),
                Throws.TypeOf<OracleException>().With.Property("Number").EqualTo(1));
        }
    }

    [Test]
    public async Task ForeignKeyClosureFollowsOwnerAndReportsAnInvisibleTarget()
    {
        using var parentSchema = OracleTestDatabase.CreateSchema();
        using var childSchema = OracleTestDatabase.CreateSchema();
        using var observerSchema = OracleTestDatabase.CreateSchema();
        using var parent = parentSchema.Connect();
        using var child = childSchema.Connect();
        using var observer = observerSchema.Connect();
        parent.Open();
        child.Open();
        observer.Open();

        QaSql.Execute(parent, """CREATE TABLE "Item.Key" (A NUMBER NOT NULL, B NUMBER, CONSTRAINT UQ_PARENT UNIQUE (B,A))""");
        QaSql.Execute(parent, $"""GRANT SELECT, REFERENCES ON "Item.Key" TO {childSchema.Name}""");
        QaSql.Execute(child, $"""
            CREATE TABLE "Item.Key" (X NUMBER, Y NUMBER,
                CONSTRAINT FK_ITEM FOREIGN KEY (Y,X) REFERENCES {parentSchema.Name}."Item.Key" (B,A))
            """);
        QaSql.Execute(child, $"""GRANT SELECT ON "Item.Key" TO {observerSchema.Name}""");

        var reader = new OracleMetadataReader();
        var inspector = new SchemaInspector();
        var childName = new MetadataName("Item.Key", childSchema.Name);
        var options = new DiscoveryOptions { Schemas = [childSchema.Name], Tables = [childName], Features = KeyFeatures };
        var filtered = await inspector.InspectAsync(child, reader, options);
        Assert.That(filtered.IsSuccess, Is.False);
        Assert.That(filtered.Diagnostics.Single(), Is.EqualTo(new DiscoveryDiagnostic("SDI005", SchemaFeature.ForeignKeys,
            MetadataCoverage.Unrepresentable, "A foreign-key target is outside the selected or visible tables; the key was omitted.", childName)));

        options = options with { IncludeReferencedTables = true };
        var closure = await inspector.InspectAsync(child, reader, options);
        AssertComplete(closure);
        var tables = closure.Schema!.Tables.Values.ToDictionary(table => table.Identity.Schema!);
        Assert.That(tables.Keys, Is.EquivalentTo(new[] { childSchema.Name, parentSchema.Name }));
        var foreignKey = tables[childSchema.Name].Constraints.OfType<ForeignKeyConstraint>().Single();
        Assert.That(foreignKey.TargetTable, Is.EqualTo(tables[parentSchema.Name].Identity));
        Assert.That(foreignKey.SourceColumns, Is.EqualTo(new[] { "Y", "X" }));
        Assert.That(foreignKey.TargetColumns, Is.EqualTo(new[] { "B", "A" }));
        Assert.That(tables[parentSchema.Name].Constraints.OfType<UniquenessConstraint>().Single().NullPolicy,
            Is.EqualTo(UniqueNullPolicy.NullsNotDistinct));
        Assert.That(() => new SchemaScriptRenderer(Dialect()).Plan(closure.Schema), Throws.Nothing);

        var restricted = await inspector.InspectAsync(observer, reader, options with { AllowPartial = true });
        Assert.That(restricted.IsSuccess, Is.True);
        Assert.That(restricted.IsComplete, Is.False);
        Assert.That(restricted.Schema!.Tables.Values.Single().Identity.Schema, Is.EqualTo(childSchema.Name));
        var diagnostic = restricted.Diagnostics.Single();
        Assert.That(diagnostic.Code, Is.EqualTo("SDI009"));
        Assert.That(diagnostic.Feature, Is.EqualTo(SchemaFeature.ForeignKeys));
        Assert.That(diagnostic.Coverage, Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(diagnostic.Object, Is.EqualTo(childName));
        Assert.That(diagnostic.Message, Does.Contain("FK_ITEM").And.Contain(parentSchema.Name).And.Contain("UQ_PARENT"));
    }

    [Test]
    public async Task StrictAndPartialInspectionReportUnsupportedMetadataAndKeepSupportedKeys()
    {
        using var schema = OracleTestDatabase.CreateSchema();
        using var connection = schema.Connect();
        connection.Open();
        QaSql.Execute(connection, """
            CREATE TABLE SAMPLE (A NUMBER DEFAULT 1, B NUMBER DEFAULT ON NULL ABS(-23), C NUMBER,
                CONSTRAINT UQ_DEFERRED UNIQUE (A,B) DEFERRABLE INITIALLY DEFERRED,
                CONSTRAINT UQ_SUPPORTED UNIQUE (C))
            """);
        var name = new MetadataName("SAMPLE", schema.Name);
        var options = new DiscoveryOptions
        {
            Tables = [name],
            Features = [.. KeyFeatures, SchemaFeature.Defaults, SchemaFeature.Generation, SchemaFeature.Checks]
        };
        var inspector = new SchemaInspector();
        var reader = new OracleMetadataReader();
        var strict = await inspector.InspectAsync(connection, reader, options);
        var partial = await inspector.InspectAsync(connection, reader, options with { AllowPartial = true });

        Assert.That(strict.IsSuccess, Is.False);
        Assert.That(partial.IsSuccess, Is.True);
        Assert.That(partial.IsComplete, Is.False);
        Assert.That(partial.Diagnostics, Is.EqualTo(strict.Diagnostics));
        Assert.That(partial.Diagnostics.Where(diagnostic => diagnostic.Code == "SDI003")
            .Select(diagnostic => (diagnostic.Feature, diagnostic.Coverage)), Is.EquivalentTo(new[]
            {
                (SchemaFeature.Generation, MetadataCoverage.Unsupported),
                (SchemaFeature.Checks, MetadataCoverage.Unsupported)
            }));
        var defaultDiagnostic = partial.Diagnostics.Single(diagnostic =>
            diagnostic.Code == "SDI009" && diagnostic.Feature == SchemaFeature.Defaults);
        Assert.That(defaultDiagnostic.Object, Is.EqualTo(name));
        Assert.That(defaultDiagnostic.Member, Is.EqualTo("B"));
        Assert.That(defaultDiagnostic.NativeDefinition, Is.EqualTo("DEFAULT ON NULL ABS(-23)"));
        Assert.That(partial.Schema!.Tables.Values.Single().Columns["A"].DefaultValue, Is.EqualTo(1m));
        var keyDiagnostic = partial.Diagnostics.Single(diagnostic =>
            diagnostic.Code == "SDI009" && diagnostic.Feature == SchemaFeature.UniqueConstraints);
        Assert.That(keyDiagnostic.Object, Is.EqualTo(name));
        Assert.That(keyDiagnostic.Message, Does.Contain("UQ_DEFERRED"));
        Assert.That(partial.Coverage[SchemaFeature.UniqueConstraints], Is.EqualTo(MetadataCoverage.Unrepresentable));
        Assert.That(partial.Schema!.Tables.Values.Single().Constraints.OfType<UniquenessConstraint>().Single().Name,
            Is.EqualTo("UQ_SUPPORTED"));
        Assert.That(partial.Schema.Indexes, Is.Empty);
    }

    private static void AssertComplete(SchemaInspectionResult result)
        => Assert.That(result.IsComplete, Is.True, string.Join(", ", result.Diagnostics.Select(item => $"{item.Code} {item.Feature} {item.Message}")));

    private static IDialect Dialect()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<OracleDialect>(["oracle"]);
        return builder.Build().Get<OracleDialect>();
    }
}
