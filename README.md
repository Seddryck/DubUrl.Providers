# DubUrl.Providers

Provider implementation packages, database-free unit tests, and real database QA for DubUrl.

The projects under Providers use released DubUrl packages. The provider unit suite compiles the tests alongside their owning provider and runs without a database.

    dotnet test QA/DubUrl.Providers.Testing
    dotnet test DubUrl.BulkCopy.Testing

The shared live fixtures are in QA/DubUrl.ProviderTesting. Their four namespaces are Schema, Connection, BulkCopy, and Query under DubUrl.Providers.QA.Contracts. Concrete fixtures supply their real driver, SQL, and expected behavior. Engine regressions live in Specific subfolders.

Prepare the Schema dependency and build the solution with:

    ./build/Initialize-SchemaQa.ps1
    dotnet build DubUrl.Providers.slnx

The initializer builds QA-only packages from a pinned Schema commit until its command-plan deployment API is available in a published package.

Run the embedded schema suites with:

    ./QA/Schema/run.ps1 -Database Sqlite
    ./QA/Schema/run.ps1 -Database DuckDb

The schema runner needs Python with defusedxml for TRX verification. PostgreSQL, SQL Server, Oracle, SingleStore, and MySQL also need Docker. Existing provider workflows under .github/workflows show the matching provisioning scripts and supported platforms.

Schema QA verifies each required shared scenario passed exactly once. See QA/Schema/README.md for capability expectations and runner details.
