using System.Data;
using DubUrl.Provider.DuckDb.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Schema.Constraints;
using DubUrl.Providers.QA.Contracts.Schema;
using DuckDB.NET.Data;
using NUnit.Framework;
using ForeignKeyConstraint = DubUrl.Schema.Constraints.ForeignKeyConstraint;

namespace DubUrl.Schema.DuckDb.QA;

[TestFixture]
[Category("DuckDb")]
[Category("Schema")]
public sealed class NativeCharacterKeyTests
{
    private static SchemaScriptRenderer Renderer()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<DuckDbDialect>(["duckdb"]);
        return new SchemaScriptRenderer(builder.Build().Get<DuckDbDialect>());
    }

    private static VarLengthColumn NativeColumn(string name, string native)
        => new(name, DbType.AnsiString, 3, nativeType: new NativeDatabaseType(native));

    [TestCase("CHAR")]
    [TestCase("VARCHAR")]
    public void Render_NativeDimensionsAppearOnceAndLogicalTypesStayInferred(string native)
    {
        var table = new Table("strings", [NativeColumn("explicit_type", native),
            new VarLengthColumn("inferred_type", DbType.AnsiString, 3),
            new VarLengthColumn("inferred_unicode", DbType.String, 3)]);
        var script = Renderer().Render(new([table]));

        Assert.That(script, Does.Contain($"explicit_type {native}(3)"));
        Assert.That(script, Does.Not.Contain("(3)(3)"));
        Assert.That(script, Does.Contain("inferred_type VARCHAR(3)"));
        Assert.That(script, Does.Contain("inferred_unicode VARCHAR(3)"));
    }

    [TestCase("CHAR")]
    [TestCase("VARCHAR")]
    public void NativeKeysEnforceConstraintsAndReadBackUnboundedVarcharValues(string native)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        var id = NativeColumn("id", native);
        var code = NativeColumn("code", native);
        var parent = new Table("parent", [id, code],
            [new PrimaryKeyConstraint([id]), new UniquenessConstraint([code])]);
        var child = new Table("child", [NativeColumn("parent_id", native)],
            [new ForeignKeyConstraint("fk_parent", ["parent_id"], "parent", ["id"])]);
        QaSql.Execute(connection, Renderer().Render(new([child, parent])));

        // DuckDB treats CHAR as VARCHAR: length is ignored and short values are not padded.
        QaSql.Execute(connection, "INSERT INTO parent VALUES ('longer than 3','long code'),('x','y'); " +
            "INSERT INTO child VALUES ('longer than 3'),('x')");
        Assert.That(() => QaSql.Execute(connection, "INSERT INTO parent VALUES ('longer than 3','new')"),
            Throws.TypeOf<DuckDBException>(), "Primary keys must reject duplicate values.");
        Assert.That(() => QaSql.Execute(connection, "INSERT INTO parent VALUES ('new','long code')"),
            Throws.TypeOf<DuckDBException>(), "Unique keys must reject duplicate values.");
        Assert.That(() => QaSql.Execute(connection, "INSERT INTO child VALUES ('missing')"),
            Throws.TypeOf<DuckDBException>(), "Foreign keys must reject missing references.");

        var columns = QaSql.Rows(connection, "SELECT data_type,character_maximum_length FROM information_schema.columns " +
            "WHERE table_name IN ('parent','child')");
        Assert.That(columns, Has.Count.EqualTo(3));
        foreach (var column in columns)
        {
            Assert.That(column[0], Is.EqualTo("VARCHAR"), "CHAR is a VARCHAR alias in DuckDB metadata.");
            Assert.That(column[1], Is.Null, "Declared string length is not retained.");
        }
        Assert.That(QaSql.Rows(connection, "SELECT id,code FROM parent ORDER BY id"),
            Is.EqualTo(new[] { new[] { "longer than 3", "long code" }, new[] { "x", "y" } }));
        Assert.That(QaSql.Rows(connection, "SELECT parent_id FROM child ORDER BY parent_id").Select(row => row[0]),
            Is.EqualTo(new[] { "longer than 3", "x" }));
    }
}
