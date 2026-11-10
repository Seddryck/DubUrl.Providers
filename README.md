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

CI live QA is disabled for Snowflake, Teradata, and SSAS Tabular and Multidimensional. SSAS keeps its named checks for its package and manifest checks only.
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
| SQL Server `JsonType` with `JsonText` | `NVARCHAR(MAX)` | Unicode text storage | Ordinary text column |
| SQL Server 2025+ `JsonType` with `Json`, qualified with `WithServerVersion` | `JSON` | JSON syntax with an object or array at the top level | Native JSON storage |
| SQL Server 2025+ native `VectorType`, qualified with `WithServerVersion` | `VECTOR(dimensions)` | Fixed dimensions and 32-bit floating-point components | Dimensions and component type from the catalog |
| Oracle 21c+ `JsonType` with `Json` and `COMPATIBLE` at least 20 | `JSON` | JSON syntax, including top-level scalars | Native JSON storage. JSON type modifiers produce an incomplete discovery diagnostic. |
| Oracle 23.4+ native `VectorType` with `COMPATIBLE` at least 23.4 | `VECTOR(dimensions, format)` | Fixed dimensions and non-null components in `INT8`, `FLOAT32`, or `FLOAT64` | Fixed dense vectors when the catalog exposes their declaration |
| DuckDB native `ArrayType`, `MapType`, and `StructType` | `type[]`, `MAP(key, value)`, and `STRUCT(...)` | Element types, struct fields, and non-null, unique map keys. List items, map values, and struct fields can be NULL. | Supported recursive shapes and field names |
| DuckDB native `VectorType` | Fixed-size numeric `type[n]` with a `list_count` check | Fixed dimensions and non-null components, up to 100,000 dimensions | Numeric fixed arrays with the generated component check |
| PostgreSQL and TimescaleDB native `ArrayType` | `type[]` with an `array_ndims` check | One dimension for nonempty arrays. Elements can be NULL. | The catalog must expose a validated rank check for complete discovery |
| PostgreSQL native `StructType`, bound with `WithCompositeType` | Schema-qualified composite from `CREATE TYPE ... AS (...)` | Ordered, typed nullable fields | Standalone composite attributes in catalog order, including supported nested composites |
| SingleStore 8.5+ native `VectorType`, qualified with `WithServerVersion` | `VECTOR(dimensions, element)` | Fixed dimensions and numeric components | Dimensions and component type from the catalog |
| MariaDB 10.5.10+ `JsonType` with `JsonText`, qualified with `WithMariaDbDefaults` | `JSON` alias for `LONGTEXT` | JSON syntax via the column's `JSON_VALID` check. Object shape is advisory. | Canonical column validation identifies JSON text |
| MariaDB 11.7.1+ native `VectorType`, qualified with `WithMariaDbDefaults` | `VECTOR(dimensions)` | Fixed dimensions and float32 components, up to 16,383 | Dimensions from the catalog |
| SQLite 3.38+ `JsonType` with `JsonText`, qualified with `WithServerVersion` | `TEXT` with a storage-class and `json_valid` check | RFC 8259 JSON text, with optional advisory shape | Renderer’s inline check in `sqlite_schema` |
| SQLite 3.45+ `JsonType` with `Binary`, qualified with `WithServerVersion` | `BLOB` with a storage-class and `json_valid(value, 8)` check | SQLite’s strictly validated JSONB representation | Renderer’s inline check in `sqlite_schema` |
| CockroachDB `JsonType` with `Json`, native `ArrayType`, and native `VectorType` | `JSONB`, `type[]`, `VECTOR(dimensions)` | JSON syntax, one-dimensional scalar arrays with nullable elements, and fixed float32 vectors | SQL declarations from `information_schema.columns` |
| CrateDB native `ArrayType`, `StructType`, and `VectorType` | `ARRAY(type)`, `OBJECT(STRICT) AS (...)`, `FLOAT_VECTOR(dimensions)` | Typed nested arrays, strict object fields, and fixed float32 vectors | Column types, object paths and policies, and vector dimensions |
| QuestDB 9.0+ native `ArrayType` | `DOUBLE[]` | One-dimensional nullable arrays and nullable double elements | Native declarations from `SHOW COLUMNS` |
| Snowflake standard tables with `JsonType` and `Json` | `VARIANT` | Semi-structured storage. The caller supplies the JSON codec and validates the object shape. | VARIANT as arbitrary JSON storage |
| Snowflake standard tables with native `ArrayType`, `MapType`, and `StructType` | Typed `ARRAY`, `MAP`, and `OBJECT` | Nested types, field order, and declared nested nullability. Map keys use text or supported integral types. | Full declarations from `DESCRIBE TABLE` where representable |
| Snowflake standard tables with native `VectorType` | `VECTOR(INT\|FLOAT, dimensions)` | Fixed dimensions and signed int32 or float32 components | Dimensions and component type from the catalog |

An optional JSON object shape describes the caller's data. Native JSON columns validate syntax within the engine's supported JSON values. The caller supplies any value codec. DuckDB native collections support reversible scalar leaves and unshaped nested JSON. Their DDL rejects required nested values and duplicate struct names under DuckDB's case-insensitive comparison. SingleStore qualifies signed integer and floating-point components up to 16,384 dimensions. SQL Server qualifies up to 1,998 single-precision vector dimensions.

DuckDB vectors support signed and unsigned 8-, 16-, 32-, and 64-bit integers and float32 or float64 components. The `ARRAY` type fixes the dimensions. The column check counts non-null components, so a nullable vector column can still store SQL NULL. Bare fixed arrays and nested vectors are outside the supported discovery shape.

PostgreSQL and TimescaleDB arrays support Boolean, signed 16-, 32-, and 64-bit integers, float32 and float64, text, binary, UUID, date, and unshaped JSONB elements. Bare rank-one declarations produce incomplete discovery because PostgreSQL accepts higher-rank values in those columns. Multidimensional declarations, non-default collations, domains, and unsupported element types or modifiers also produce incomplete discovery. TimescaleDB discovery reads the hypertable's column definitions and excludes the extension's default internal schemas. Hypertable partitioning is outside schema rendering.

For the opt-in PostgreSQL and TimescaleDB live tests in `PostgreSqlArrayLiveTests`, set `DUBURL_POSTGRES_ARRAY_QA_URL` or `DUBURL_TIMESCALE_ARRAY_QA_URL` to the corresponding disposable database and select the individual test by its fully qualified name.

Bind each PostgreSQL struct shape to a schema-qualified name with `PgsqlDialect.WithCompositeType(name, shape)`. Register nested composites first. Use `RenderCompositeTypeDefinitions()` to provision new types before executing the table plan. An existing matching type needs only the binding. The table planner reuses that name across columns. Discovery recovers the portable shape, and the raw metadata exposes the qualified name on `MetadataColumn.NativeType` for rebinding. Supported attributes use the same scalar leaves as PostgreSQL arrays, unshaped JSONB, or another bound composite. Required fields, domains, table row types, and unsupported type modifiers are outside this mapping. The opt-in tests in `PostgreSqlCompositeLiveTests` use `DUBURL_POSTGRES_COMPOSITE_QA_CONNECTION_STRING`.

For Oracle, use `WithServerVersion(serverVersion, compatibilityVersion)` when the database's `COMPATIBLE` setting differs from the server release. The one-argument overload assumes they match. Oracle qualifies fixed dense vectors up to 65,535 dimensions. Flexible dimensions or formats, sparse storage, and binary vectors produce incomplete discovery diagnostics.

MariaDB discovery treats `LONGTEXT COLLATE utf8mb4_bin` as JSON text when the catalog also shows the matching column-level `JSON_VALID` check. The `JSON` alias recreates that check during rendering. MariaDB has no native array, map, or struct column mapping. Set `DUBURL_MARIADB_STRUCTURED_QA_CONNECTION_STRING` to a disposable MariaDB 11.7.1+ database to run the focused round-trip test:

    dotnet test QA/DubUrl.Providers.Testing -f net10.0 --filter "FullyQualifiedName=DubUrl.Provider.MySql.Tests.Schema.MariaDbStructuredTypeLiveTests.JsonTextAndVectorRoundTrip"

Use each engine’s `WithServerVersion` to qualify CockroachDB vectors (24.2+, up to 16,000 dimensions), CrateDB vectors (5.5+, up to 2,048 dimensions), and QuestDB arrays (9.0+). Catalog discovery covers tables and columns on these routes. CrateDB supports nullable object fields and nullable array elements. CrateDB text arrays are outside discovery because the catalog also reports bounded character arrays as `text_array`. QuestDB higher-rank arrays produce incomplete discovery because the column model has no rectangular tensor type.

Use a SQLite build with JSON functions enabled. Callers can write JSONB using `jsonb(?1)` and read it as JSON text using `json(column)`. Discovery distinguishes the renderer’s validated forms from ordinary `TEXT` and `BLOB`, and reports bare `JSON` and `JSONB` declarations as incomplete. Native array, map, struct, and vector column shapes are outside SQLite’s structured mappings.

For the three opt-in round-trip tests, set `DUBURL_COCKROACH_STRUCTURED_QA_CONNECTION_STRING`, `DUBURL_CRATE_STRUCTURED_QA_CONNECTION_STRING`, or `DUBURL_QUEST_STRUCTURED_QA_CONNECTION_STRING` for the corresponding disposable engine. Use QuestDB’s Npgsql `Server Compatibility Mode=NoTypeLoading`. Select the individual test by its fully qualified name in `PgCompatibleStructuredLiveTests`.

Snowflake collection leaves support Boolean, text, date, double, and signed 16-, 32-, and 64-bit integers. Integral leaves use scale-zero `NUMBER` with declared precision. Vectors are supported as top-level columns up to 4,096 dimensions. Snowflake discovery covers standard tables, columns, and comments in the current database or the databases selected by the caller. Bare semi-structured arrays and objects, unsupported leaf declarations, and unavailable type details produce incomplete discovery diagnostics.

Set `DUBURL_SNOWFLAKE_QA_CONNECTION_STRING` to a disposable Snowflake schema to run the opt-in round-trip test:

    dotnet test QA/DubUrl.Providers.Testing -f net10.0 --filter "FullyQualifiedName=DubUrl.Provider.Snowflake.Tests.Schema.SnowflakeStructuredTypeLiveTests.AllFiveShapesRoundTrip"

Use `SchemaInspector` with the registered provider reader to recover structured descriptors. Recognized types outside the model, such as DuckDB fixed arrays without a recognized component check or SingleStore vectors with unrepresentable component types, produce an incomplete discovery diagnostic. Legacy scalar columns and native type hints retain their rendering path.
