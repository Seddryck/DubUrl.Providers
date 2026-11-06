using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;
namespace DubUrl.Provider.CrateDb.QA.infrastructure;
public sealed class QaManifestTests
{
    [Test, Category("Manifest")]
    public void ManifestIsValid() => QaManifest.Validate(Path.Combine(AppContext.BaseDirectory, "qa-manifest.json"), "cratedb", "Connection", "Querying");
}
