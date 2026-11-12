# Connection URL catalog

Provider projects own their profile fragments under `Mapping/ConnectionUrls/`. The shared ODBC project owns the ODBC fragments. The `DubUrl.OleDb/` and `DubUrl.Adomd/` directories here contain the profiles for those separately packaged implementations. A fragment contains `routes` and can declare its own `locations`, `authentication`, and `tlsPolicies`. `catalog-source.json` owns reusable definitions and lists the profile IDs in presentation order.

Each profile selects a registered mapper using its preferred URL scheme and provider invariant name. The composer checks every emitted scheme against that registration and adds the registered schemes and capabilities declared by the mapper. TLS choices describe the builder's available settings for a particular driver. A declared TLS configuration names the mapper's configuration strategy.

Run the focused tests to generate the catalog and exercise representative URLs through the registered providers.

```powershell
dotnet test QA/DubUrl.Providers.Testing -c Release -f net10.0 -p:TargetFrameworks=net10.0 --filter FullyQualifiedName~ConnectionUrlCatalogTest
```

The Provider packages workflow creates the release artifact from the JSON used by the tests. Dispatch it on `main` with a new provider package version and `publish` enabled to publish both outputs. The catalog uses the same version and source commit as the NuGet packages. For example, the catalog for provider packages 0.68.0 has tag `connection-url-catalog-v0.68.0`. The workflow verifies the published NuGet source revisions before releasing the catalog and preserves existing release bytes on retries.

Consumers pin the exact release tag and the SHA-256 of `connection-url-catalog.zip`. The archive contains `catalog.json`, `schema.json`, and `manifest.json`. The manifest records the catalog and schema versions, the Providers source commit, and the Core package version used to build and test the registrations. Check the schema version against the consumer and the Core version and Providers source revision against the provider packages the consumer supports. The CLI can bundle the same verified catalog for offline use.
