# Connector packs

The [connector matrix](connector-matrix.json) selects provider and driver combinations. The
[catalog exporter](Catalog/Program.cs) discovers the matching provider attributes, groups equivalent
payloads, and preserves their installation aliases. [ProviderBinding.Build](../Providers/Build/ProviderBinding.Build.csproj)
collects the provider, driver, native assets, and optional adapters. See the [redistribution notices](THIRD-PARTY-NOTICES.md).

The [Provider packages workflow](../.github/workflows/provider-packages.yml) resolves the latest listed
stable `DubUrl` package from NuGet.org once per build. The release uses the provider package version
for its archive names and the resolved DubUrl version for the minimum API in each manifest. The build
reads the shared dependency inventory embedded in that Core assembly to omit host-supplied assemblies.
Validation uses the same Core package to install, load, and remove the resulting packs.

The workflow publishes the validated ZIPs and `connector-catalog.json` to
[DubUrl.Providers Releases](https://github.com/Seddryck/DubUrl.Providers/releases) at
`connectors-v<provider-version>`. Stable connector releases are marked as latest. Older installations
can continue to use their existing catalogs and download URLs. The `connection-url-catalog` release
contains the separate metadata bundle used by the URL builder.

The workflow also publishes each release ZIP to GHCR as an OCI artifact tagged
`ghcr.io/seddryck/duburl-connectors/<connector-id>:<provider-version>`. The workflow summary
lists the digest-pinned references. Pull a ZIP by digest with ORAS, set
`DUBURL_ProviderPacks__CatalogPath` to the matching `connector-catalog.json` from the GitHub Release,
and run `duburl connector install <connector-id> --archive <zip>`.

The workflow's `GITHUB_TOKEN` creates new packages. For an existing package linked to
another repository, grant `Seddryck/DubUrl.Providers` **Write** under **Manage Actions access**
in the package settings. This applies to the PostgreSQL pilot package linked to DubUrl Core.

To build and validate locally, use the same Core version for both commands and an empty output directory.

```powershell
$coreVersion = ./build/Get-LatestDubUrlVersion.ps1
./connectors/Build-ConnectorArtifacts.ps1 -Version 0.70.0 -CoreVersion $coreVersion
./connectors/Test-ConnectorArtifacts.ps1 -Version 0.70.0 -CoreVersion $coreVersion -ArtifactDirectory artifacts/connectors
```
