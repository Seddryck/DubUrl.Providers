using DubUrl.ProviderTesting;
using NUnit.Framework;

namespace DubUrl.Provider.Databricks.QA.Infrastructure;

public sealed class QaManifestTests
{
    [Test, Category("Manifest")]
    public void ManifestIsValid()
        => QaManifest.Validate(Path.Combine(AppContext.BaseDirectory, "qa-manifest.json"),
            "databricks", "Connection", "Querying", "ODBC");
}
