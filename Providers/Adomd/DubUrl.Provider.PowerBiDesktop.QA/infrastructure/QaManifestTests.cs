using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using NUnit.Framework;
namespace DubUrl.Provider.PowerBiDesktop.QA.infrastructure;
public sealed class QaManifestTests { [Test, Category("Manifest")] public void ManifestIsValid() => QaManifest.Validate(Path.Combine(AppContext.BaseDirectory, "qa-manifest.json"), "powerbidesktop", "Connection", "Querying", "ADOMD"); }
