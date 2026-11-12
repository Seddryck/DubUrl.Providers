using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using DubUrl.BulkCopy;
using DubUrl.Mapping;
using Moq;
using NUnit.Framework;

namespace DubUrl.Provider.Odbc.Tests.Sql;

public sealed class DatabricksFileLoaderTests
{
    private const string DatabricksUrl = "odbc+databricks://example.com/warehouse/qa?Driver=Explicit";
    private const string VolumePath = "/Volumes/catalog/schema/volume/batch-001/";

    [Test]
    public void RegistersFileLoadingOnDatabricksOnly()
    {
        var registry = CreateRegistry();
        var databricks = new ConnectionUrl(DatabricksUrl, registry);
        var spark = new ConnectionUrl("odbc+spark://example.com?Driver=Explicit", registry);

        Assert.Multiple(() =>
        {
            Assert.That(databricks.TryGetCapability<FileLoadCapability>(out _), Is.True);
            Assert.That(spark.TryGetCapability<FileLoadCapability>(out _), Is.False);
            Assert.That(databricks.TryGetCapability<IBulkCopyProvider>(out _), Is.False);
        });
    }

    [TestCase("orders", VolumePath, "parquet", "`orders`", VolumePath, "PARQUET")]
    [TestCase("sales.orders", VolumePath, "JSON", "`sales`.`orders`", VolumePath, "JSON")]
    [TestCase("analytics.sales.qa`-orders", "/Volumes/catalog/schema/volume/O'Brien\\batch/",
        "CSV", "`analytics`.`sales`.`qa``-orders`", "/Volumes/catalog/schema/volume/O\\'Brien\\\\batch/", "CSV")]
    public void ExecutesCopyIntoWithQuotedTargetAndSource(string target, string path, string format,
        string quotedTarget, string escapedPath, string formatKeyword)
    {
        var command = new Mock<IDbCommand>();
        command.SetupProperty(x => x.CommandText);
        var connection = new Mock<IDbConnection>();
        connection.Setup(x => x.CreateCommand()).Returns(command.Object);
        var url = new TestConnectionUrl(CreateRegistry(), () => connection.Object);

        url.LoadFromFiles(target, path, format);

        Assert.That(command.Object.CommandText, Is.EqualTo($"COPY INTO {quotedTarget} FROM '{escapedPath}' FILEFORMAT = {formatKeyword}"));
        command.Verify(x => x.ExecuteNonQuery(), Times.Once);
        command.Verify(x => x.Dispose(), Times.Once);
        connection.Verify(x => x.Dispose(), Times.Once);
    }

    [TestCase("analytics..orders", VolumePath, "PARQUET", "tableName")]
    [TestCase("a.b.c.d", VolumePath, "PARQUET", "tableName")]
    [TestCase("orders", "/tmp/file.parquet", "PARQUET", "sourcePath")]
    [TestCase("orders", "/Volumes/catalog/schema", "PARQUET", "sourcePath")]
    [TestCase("orders", "/Volumes/catalog/schema/volume/../other/", "PARQUET", "sourcePath")]
    [TestCase("orders", VolumePath, "PARQUET COPY_OPTIONS ('force' = 'true')", "fileFormat")]
    public void RejectsInvalidArgumentsBeforeOpeningAConnection(string target, string path, string format, string parameter)
    {
        var url = new TestConnectionUrl(CreateRegistry(), () => throw new AssertionException("Unexpected connection open."));

        Assert.That(() => url.LoadFromFiles(target, path, format),
            Throws.ArgumentException.With.Property("ParamName").EqualTo(parameter));
    }

    [Test]
    public void DisposesTheCommandAndConnectionOnFailure()
    {
        var failure = new InvalidOperationException("Load failed.");
        var command = new Mock<IDbCommand>();
        command.Setup(x => x.ExecuteNonQuery()).Throws(failure);
        var connection = new Mock<IDbConnection>();
        connection.Setup(x => x.CreateCommand()).Returns(command.Object);
        var url = new TestConnectionUrl(CreateRegistry(), () => connection.Object);

        Assert.That(Assert.Throws<InvalidOperationException>(() => url.LoadFromFiles("orders", VolumePath, "PARQUET")),
            Is.SameAs(failure));
        command.Verify(x => x.Dispose(), Times.Once);
        connection.Verify(x => x.Dispose(), Times.Once);
    }

    private static SchemeRegistry CreateRegistry()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        return new ProviderCatalogBuilder().AddApplicationProviders().Build();
    }

    private sealed class TestConnectionUrl(SchemeRegistry registry, Func<IDbConnection> open) : ConnectionUrl(DatabricksUrl, registry)
    {
        public override IDbConnection Open() => open();
    }
}
