# DubUrl.Providers

Provider implementation packages, database-free unit tests, and real database QA for DubUrl.

The projects under Providers use released DubUrl Core packages. The provider unit suite compiles the tests alongside their owning provider and runs without a database.

    dotnet test QA/DubUrl.Providers.Testing
    dotnet test DubUrl.BulkCopy.Testing

The shared live fixtures are in QA/DubUrl.Provider.QA. Their four namespaces are Schema, Connection, BulkCopy, and Query under DubUrl.Providers.QA.Contracts. Concrete fixtures supply their real driver, SQL, and expected behavior. Engine regressions live in Specific subfolders.

Build the solution using the published Schema packages:

    dotnet build DubUrl.Providers.slnx

Schema source-compatibility QA can opt into a local package by running `./build/Initialize-SchemaQa.ps1 -SourceCommit <sha>` before building in the same PowerShell session. The initializer selects the candidate for that session or the current GitHub Actions job.

Run the embedded schema suites with:

    ./QA/Schema/run.ps1 -Database Sqlite
    ./QA/Schema/run.ps1 -Database DuckDb

The schema runner needs Python with defusedxml for TRX verification. PostgreSQL, SQL Server, Oracle, SingleStore, and MySQL also need Docker. Existing provider workflows under .github/workflows show the matching provisioning scripts and supported platforms.

Schema QA verifies each required shared scenario passed exactly once. See QA/Schema/README.md for capability expectations and runner details.

Every active provider QA job requires a passing live connection lifecycle, customer query, schema discovery capability check, and bulk-copy capability check. Native bulk-copy adapters also inherit the live row and null round trip. Schema providers continue to run the seven full schema suites with the mandatory scenario manifest.

Live QA is disabled for Snowflake, Teradata, and SSAS Tabular and Multidimensional. SSAS keeps its named checks for its package and manifest checks only.
The required Apache Drill QA check builds the provider and validates its manifest. Live ODBC QA requires an authenticated driver distribution and is excluded on public runners.

The published [DubUrl.ProviderTesting 0.60.0 package](https://www.nuget.org/packages/DubUrl.ProviderTesting/0.60.0) remains available for compatibility. The internal DubUrl.Provider.QA project is not published.
