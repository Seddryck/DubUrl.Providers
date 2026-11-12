using System.Data.Common;
using System.Data.Odbc;
using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Querying;
using NUnit.Framework;

namespace DubUrl.Provider.Hive.QA.Connection;

public sealed class HiveOdbcTests
{
    [Test]
    [Category("Live"), Category("ODBC")]
    public void OpenHiveServer2()
        => OdbcSmokeTest.Verify(new ConnectionUrl(HiveQaDatabase.GetUrl(useNativeQuery: true)), OdbcFactory.Instance);

    [Test, Category("Live"), Category("ODBC")]
    public void ExecutesGeneratedHiveQlWithQuotedIdentifiers()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        using var database = new DatabaseUrl(new ConnectionUrl(HiveQaDatabase.GetUrl(useNativeQuery: true)), new(), NullQueryLogger.Instance);

        const string query = """
            SELECT $qualifiedColumn; format="identity"$
            FROM (SELECT 1 AS $column; format="identity"$) $source; format="identity"$
            """;
        var result = database.ReadScalar<int>(query, new Dictionary<string, object?>
        {
            ["qualifiedColumn"] = "sample.select",
            ["column"] = "select",
            ["source"] = "sample",
        });

        Assert.That(result, Is.EqualTo(1));
    }

    [Test, Category("Manifest")]
    public void ManifestIsValid()
        => QaManifest.Validate(Path.Combine(AppContext.BaseDirectory, "qa-manifest.json"),
            "hive", "Connection", "Querying", "ODBC");
}
