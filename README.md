# DubUrl.Providers

Provider implementation packages, database-free unit tests, and real database QA for DubUrl.

The projects under Providers use released DubUrl Core packages. Until a published Schema package includes the structured type contract, initialize the pinned local Schema feed in the same PowerShell session before building or testing:

    ./build/Initialize-SchemaQa.ps1
    dotnet build DubUrl.Providers.slnx
    dotnet test QA/DubUrl.Providers.Testing
    dotnet test DubUrl.BulkCopy.Testing

The provider unit suite compiles the tests alongside their owning provider and runs without a database. Schema source-compatibility QA can select another commit with `./build/Initialize-SchemaQa.ps1 -SourceCommit <sha>`.

The shared live fixtures are in QA/DubUrl.Provider.QA. Their four namespaces are Schema, Connection, BulkCopy, and Query under DubUrl.Providers.QA.Contracts. Concrete fixtures supply their real driver, SQL, and expected behavior. Engine regressions live in Specific subfolders.

Run the embedded schema suites with:

    ./QA/Schema/run.ps1 -Database Sqlite
    ./QA/Schema/run.ps1 -Database DuckDb

The schema runner needs Python with defusedxml for TRX verification. PostgreSQL, SQL Server, Oracle, SingleStore, and MySQL also need Docker. Existing provider workflows under .github/workflows show the matching provisioning scripts and supported platforms.

Schema QA verifies each required shared scenario passed exactly once. See QA/Schema/README.md for capability expectations and runner details.

Every active provider QA job requires a passing live connection lifecycle, customer query, schema discovery capability check, and bulk-copy capability check. Native bulk-copy adapters also inherit the live row and null round trip. Schema providers continue to run the seven full schema suites with the mandatory scenario manifest.

Live QA is disabled for Snowflake, Teradata, and SSAS Tabular and Multidimensional. SSAS keeps its named checks for its package and manifest checks only.
The required Apache Drill QA check builds the provider and validates its manifest. Live ODBC QA requires an authenticated driver distribution and is excluded on public runners.

The published [DubUrl.ProviderTesting 0.60.0 package](https://www.nuget.org/packages/DubUrl.ProviderTesting/0.60.0) remains available for compatibility. The internal DubUrl.Provider.QA project is not published.

## Structured schema columns

Schema rendering uses the column's explicit `StructuredStorage`. The following table describes SQL representation, database enforcement, and catalog recovery independently.

| Provider and descriptor | Representation | Database enforcement | Catalog recovery |
| --- | --- | --- | --- |
| DuckDB `JsonType` with `Json` | `JSON` | JSON syntax | JSON storage and arbitrary JSON shape |
| PostgreSQL `JsonType` with `Json` | `JSONB` | JSON syntax | JSONB as arbitrary JSON. Existing `json` native hints are retained. |
| MySQL 8+ `JsonType` with `Json`, qualified with `WithInnoDbDefaults` | `JSON` | JSON syntax | JSON storage and arbitrary JSON shape |
| DuckDB `JsonType` with `JsonText` | `VARCHAR` | Text storage | Ordinary text column |
| DuckDB native `ArrayType`, `MapType`, and `StructType` | `type[]`, `MAP(key, value)`, and `STRUCT(...)` | Element types, struct fields, and non-null, unique map keys. List items, map values, and struct fields can be NULL. | Supported recursive shapes and field names |
| SingleStore 8.5+ native `VectorType`, qualified with `WithServerVersion` | `VECTOR(dimensions, element)` | Fixed dimensions and numeric components | Dimensions and component type from the catalog |

An optional JSON object shape describes the caller's data. JSON column types validate syntax and accept other valid JSON values. The caller supplies any value codec. DuckDB native collections support reversible scalar leaves and unshaped nested JSON. Their DDL rejects required nested values and duplicate struct names under DuckDB's case-insensitive comparison. SingleStore qualifies signed integer and floating-point components up to 16,384 dimensions.

Use `SchemaInspector` with the registered provider reader to recover structured descriptors. Recognized types outside the model, such as DuckDB fixed arrays or SingleStore vectors with unrepresentable component types, produce an incomplete discovery diagnostic. Legacy scalar columns and native type hints retain their rendering path.
