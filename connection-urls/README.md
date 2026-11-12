# Connection URL catalog

Provider projects own their profile fragments under `Mapping/ConnectionUrls/`. The shared ODBC project owns the ODBC fragments. The `DubUrl.OleDb/` and `DubUrl.Adomd/` directories here contain the profiles for those separately packaged implementations. A fragment contains `routes` and can declare its own `locations`, `authentication`, and `tlsPolicies`. `catalog-source.json` owns reusable definitions and lists the profile IDs in presentation order.

Each profile selects a registered mapper using its preferred URL scheme and provider invariant name. The composer checks every emitted scheme against that registration and adds the registered schemes and capabilities declared by the mapper. TLS choices describe the builder's available settings for a particular driver. A declared TLS configuration names the mapper's configuration strategy.

Run the focused tests to generate the catalog and exercise representative URLs through the registered providers.

```powershell
dotnet test QA/DubUrl.Providers.Testing -c Release -f net10.0 -p:TargetFrameworks=net10.0 --filter FullyQualifiedName~ConnectionUrlCatalogTest
```

The Connection URL catalog workflow validates the catalog and creates the release artifact from the JSON used by the tests. Dispatch the workflow on `main` with a new stable catalog version to publish it. Catalog releases have independent versions and use tags such as `connection-url-catalog-v1.0.0`. The workflow preserves existing release bytes on retries. Provider NuGet package publishing follows its own workflow.

Consumers pin the exact release tag and the SHA-256 of `connection-url-catalog.zip`. The archive contains `catalog.json`, `schema.json`, and `manifest.json`. The manifest records the catalog and schema versions, the Providers source commit, and the Core package version used to build and test the registrations. Check the schema version against the consumer and the Core version and Providers source revision against the provider packages the consumer supports. The CLI can bundle the same verified catalog for offline use.
