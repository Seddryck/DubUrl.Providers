using Index = DubUrl.Schema.Index;
using Constraints = DubUrl.Schema.Constraints;
using DubUrl.Schema;
using SchemaModel = DubUrl.Schema.Schema;
using System.Data;
using System.Data.Common;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Builders;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Schema;

public abstract partial class ProviderContract
{
    [TestCase(false)]
    [TestCase(true)]
    [Category("PublicCommandPlan")]
    public void PublicCommandPlan_TablesCommentsAndViewChain(bool fluent)
    {
        using var target = CreatePublicDatabase();
        var provider = target.Url.Dialect.SchemaSqlRenderer;
        var dialect = target.Url.Dialect;
        var prefix = "Cp" + Guid.NewGuid().ToString("N")[..8];
        var first = new DatabaseObjectName(prefix + "a");
        var second = new DatabaseObjectName(prefix + "b");
        var view = new DatabaseObjectName(prefix + "v");
        var dependent = new DatabaseObjectName(prefix + "w");
        var supportsComments = provider.SupportsCommentRendering;
        const string description = "description; with 'quotes' -- and /* delimiters */";
        string Quote(DatabaseObjectName name) => provider.RenderObjectName(dialect, name.Catalog, name.Database, name.Schema, name.Name);
        ISchemaBuilder Build(ITableCollectionBuilder builder) => builder.WithTables(tables => tables
            .Add(table => table.WithIdentity(first).WithColumns(columns => columns
                .Add(column => column.WithName("id").WithType(DbType.Int32).WithDescription(supportsComments ? description : null)))
                .WithDescription(supportsComments ? description : null))
            .Add(table => table.WithIdentity(second).WithColumns(columns => columns
                .Add(column => column.WithName("id").WithType(DbType.Int32).WithDescription(supportsComments ? description : null)))
                .WithDescription(supportsComments ? description : null)))
            .WithViews(views => views
                .Add(item => item.WithIdentity(view).From(first).WithProjections(projections => projections
                    .Add(projection => projection.WithColumn("id"))))
                .Add(item => item.WithIdentity(dependent).From(view).WithProjections(projections => projections
                    .Add(projection => projection.WithColumn("id")))));
        try
        {
            if (fluent) target.Url.DeploySchema(Build);
            else target.Url.DeploySchema(Build(new SchemaBuilder()).Build());
            using var connection = (DbConnection)target.Url.Open();
            QaSql.Execute(connection, $"INSERT INTO {Quote(first)} VALUES (42)");
            Assert.That(Convert.ToInt32(QaSql.Rows(connection, $"SELECT * FROM {Quote(dependent)}").Single()[0]), Is.EqualTo(42));
            Assert.That(ObjectExists(connection, second, false), Is.True);
            if (supportsComments)
            {
                Assert.That(ReadComments(connection, first).Table, Is.EqualTo(description));
                Assert.That(ReadComments(connection, second).Table, Is.EqualTo(description));
                Assert.That(ReadComments(connection, first).Columns["id"], Is.EqualTo(description));
                Assert.That(ReadComments(connection, second).Columns["id"], Is.EqualTo(description));
            }
            // Exercise the same public plan's reverse drop order and replacement path.
            target.Url.DeploySchema(Build(new SchemaBuilder()).Build(), SchemaCreationOptions.DropIfExists);
            Assert.That(Convert.ToInt64(QaSql.Rows(connection, $"SELECT COUNT(*) FROM {Quote(dependent)}").Single()[0]), Is.Zero);
        }
        finally
        {
            using var connection = (DbConnection)target.Url.Open();
            foreach (var (name, isView) in new[] { (dependent, true), (view, true), (second, false), (first, false) })
            {
                if (ObjectExists(connection, name, isView))
                {
                    var command = new SchemaSqlCommand($"DROP {(isView ? "VIEW" : "TABLE")} {Quote(name)};",
                        isView ? SchemaSqlOperation.DropView : SchemaSqlOperation.DropTable, name.ToString());
                    // Cleanup uses the production provider contract, never QA statement splitting.
                    new SchemaScriptDeployer().DeploySchema(target.Url, provider.PlanCommands(dialect, [command]));
                }
                Assert.That(ObjectExists(connection, name, isView), Is.False, "Public deployment leaked an object.");
            }
        }
    }

    [Test]
    [Category("PublicCommandPlan")]
    public void PublicCommandPlan_IndexesAndSupportedForeignKeysSurviveReplacement()
    {
        using var target = CreatePublicDatabase();
        var dialect = target.Url.Dialect;
        var provider = dialect.SchemaSqlRenderer;
        var prefix = "Ck" + Guid.NewGuid().ToString("N")[..8];
        var parent = new Table(prefix + "a", [new Column("id", DbType.Int32,
            constraints: [new Constraints.PrimaryKeyConstraint()])]);
        var foreignKeys = FeatureExpectations["ForeignKey"].Outcome == ExpectedOutcome.Supported;
        var child = new Table(new DatabaseObjectName(prefix + "b"), [Id("b")], foreignKeys
            ? [new Constraints.ForeignKeyConstraint(prefix + "fk", ["b"], parent.Identity, ["id"])]
            : [], null, PhysicalOptions("UniqueIndex"));
        var index = new Index(prefix + "ix", child.Name, [new IndexColumn("b")], isUnique: true);
        var schema = new SchemaModel([parent, child], [index]);
        string Quote(Table table) => provider.RenderObjectName(dialect, null, null, null, table.Name);
        try
        {
            target.Url.DeploySchema(schema);
            using var connection = (DbConnection)target.Url.Open();
            QaSql.Execute(connection, $"INSERT INTO {Quote(parent)} VALUES (1)");
            QaSql.Execute(connection, $"INSERT INTO {Quote(child)} VALUES (1)");
            Assert.That(() => QaSql.Execute(connection, $"INSERT INTO {Quote(child)} VALUES (1)"), Throws.InstanceOf<DbException>());
            // Replacement exercises dependency-ordered drops, implicit index removal and recreation.
            target.Url.DeploySchema(schema, SchemaCreationOptions.DropIfExists);
            Assert.That(Convert.ToInt64(QaSql.Rows(connection, $"SELECT COUNT(*) FROM {Quote(child)}").Single()[0]), Is.Zero);
            QaSql.Execute(connection, $"INSERT INTO {Quote(parent)} VALUES (2)");
            QaSql.Execute(connection, $"INSERT INTO {Quote(child)} VALUES (2)");
            Assert.That(() => QaSql.Execute(connection, $"INSERT INTO {Quote(child)} VALUES (2)"), Throws.InstanceOf<DbException>());
        }
        finally
        {
            using var connection = (DbConnection)target.Url.Open();
            foreach (var table in new[] { child, parent })
            {
                if (ObjectExists(connection, table.Identity, false))
                {
                    new SchemaScriptDeployer().DeploySchema(target.Url, provider.PlanCommands(dialect,
                        [new($"DROP TABLE {Quote(table)};", SchemaSqlOperation.DropTable, table.Name)]));
                }
                Assert.That(ObjectExists(connection, table.Identity, false), Is.False);
            }
        }
    }

    [Test]
    [Category("PublicCommandPlan")]
    public void PublicCommandPlan_PartialFailureReportsCommandAndAllowsCleanup()
    {
        using var target = CreatePublicDatabase();
        var dialect = target.Url.Dialect;
        var provider = dialect.SchemaSqlRenderer;
        var prefix = "Cf" + Guid.NewGuid().ToString("N")[..8];
        var first = new Table(prefix + "a", [new Column("id", DbType.Int32)]);
        var duplicate = new Table(prefix + "b", [new Column("id", DbType.Int32)]);
        var last = new Table(prefix + "c", [new Column("id", DbType.Int32)]);
        try
        {
            target.Url.DeploySchema(new SchemaModel([duplicate]));
            var error = Assert.Catch<DbException>(() => target.Url.DeploySchema(new SchemaModel([first, duplicate, last])))!;
            Assert.That(error.Data["SchemaCommandIndex"], Is.EqualTo(1));
            Assert.That(error.Data["SchemaCommandOperation"], Is.EqualTo("CreateTable"));
            Assert.That(error.Data["SchemaCommandObject"], Is.EqualTo(duplicate.Name));
            using var connection = (DbConnection)target.Url.Open();
            Assert.That(ObjectExists(connection, first.Identity, false), Is.True);
            Assert.That(ObjectExists(connection, last.Identity, false), Is.False);
        }
        finally
        {
            using var connection = (DbConnection)target.Url.Open();
            foreach (var table in new[] { last, duplicate, first })
            {
                if (ObjectExists(connection, table.Identity, false))
                {
                    var name = provider.RenderObjectName(dialect, null, null, null, table.Name);
                    new SchemaScriptDeployer().DeploySchema(target.Url, provider.PlanCommands(dialect,
                        [new($"DROP TABLE {name};", SchemaSqlOperation.DropTable, table.Name)]));
                }
                Assert.That(ObjectExists(connection, table.Identity, false), Is.False);
            }
        }
    }
}
