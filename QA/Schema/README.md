# Real-database schema QA

## Published schema coverage

The Build and schema QA workflow collects the TRX reports from the shared database matrix,
its Spark, Databricks, and Hive ODBC jobs, and the provider unit tests. It packages the
report as the `schema-coverage` workflow artifact. On `main`, it publishes the snapshot on the dedicated
`schema-coverage` branch. Consumers can retrieve
[schema-coverage.v1.json](https://raw.githubusercontent.com/Seddryck/DubUrl.Providers/schema-coverage/schema-coverage.v1.json)
before building documentation.

The report includes all databases in `connection-urls/catalog-source.json`. A database can
have multiple QA targets, such as PostgreSQL 17 and 18 or SQL Server 2022 and 2025. Spark needs the licensed Simba
runner and the repository's `SPARK_ODBC_DRIVER_VERSION` variable. Databricks uses the
`databricks-qa` environment. Hive provisions HiveServer2 in its existing ODBC job.

### Report format

- `schemaVersion` is `1`. `providerCommit`, `runUrl`, `dubUrlVersion`, and
  `dubUrlSchemaVersion` describe the source, CI run, and tested packages.
- `features` lists the ordered table rows as `id`, `label`, and `group`.
- `providers` lists the table columns as `id`, `label`, and `targets`. Each target has an
  `id`, an engine `variant`, the name of its QA `artifact` when present, and a `coverage`
  object keyed by feature ID. The `runUrl` identifies the workflow that produced the artifacts.
  A provider without a configured schema QA target has one target with a null variant.
- Each feature has an `inspect` cell and a `generate` cell. A cell has a `status` and an
  `evidence` array. Evidence records the test identifier, coverage status, distinct TRX
  outcomes, and a qualification when one applies. An evidence item can name a separate
  `artifact`, such as the provider unit test report in `schema-qa-rejections`.

The statuses are `supported`, `limited`, `unsupported`, `unverified`, and `failed`.
A passing test supports its declared expectation, including expected rejections.
A failed mapped test takes precedence. Missing or skipped tests produce unverified coverage.
If mapped tests pass with different coverage statuses, the cell reports limited coverage.

Keep feature IDs stable in `coverage-cases.json`. Map shared scenarios to the operation
they exercise and add focused rules for tests that verify individual features or variants.
Use `rendererRejections` for explicit DDL planning rejections and `inspectionRejections`
for metadata reader features explicitly reported as unsupported. Each target and feature
gets a separate test. For inspection, a direct reader check takes precedence over shared
fixtures that can stop before reading the catalog. A shared fixture's rejected setup DDL
does not establish whether the reader can inspect the feature.

To check downloaded artifacts locally, keep each live report in its `schema-qa-<target>`
directory and the provider unit test TRX in `schema-qa-rejections`. Then run the validator
and coverage generator.

```sh
python -m pip install defusedxml==0.7.1
python -m unittest discover -s QA/Schema
python QA/Schema/build_coverage.py --artifacts artifacts/schema-reports \
  --output artifacts/schema-coverage/schema-coverage.v1.json \
  --commit "$(git rev-parse HEAD)" --run-url 'https://github.com/Seddryck/DubUrl.Providers/actions/runs/RUN_ID'
```

## Running database fixtures

```powershell
./QA/run.ps1 -Database Sqlite -Framework net10.0
./QA/run.ps1 -Database DuckDb -Framework net10.0
./QA/run.ps1 -Database PostgreSql -Framework net10.0
```

The runner builds only the selected fixture and framework, provisions a disposable database on a free local port, waits for readiness, requires nonzero passing tests with no skips, saves redacted logs and TRX reports, and removes its own container/volumes in `finally`. It never connects to an existing database. Remove old `artifacts/results/<Database>/<Framework>` output before repeating a run. PostgreSQL, SQL Server, Oracle, SingleStore and MySQL require Docker. SQLite and DuckDB run their embedded engines. Oracle and SingleStore also retain the original pinned images and startup readiness support. PostgreSQL retains its original pinned image. SQL Server defaults to its 2022 CU image. Set `DUBURL_SQLSERVER_QA_IMAGE` to run the same suite against a different image.

CI runs the database suites on .NET 10, including separate runs for PostgreSQL 17 and 18 and for SQL Server 2022 and 2025. The unit matrix still covers .NET 8, 9 and 10, with embedded DuckDB integration cases in the .NET 10 entry. PostgreSQL, SQL Server, Oracle, SingleStore and MySQL use Linux. SQLite and DuckDB use Windows. SchemaContract and DiscoveryContract are shared internally through `DubUrl.Schema.QA`, which is not published as a NuGet package. Provider connection, raw driver metadata and application-level integration tests remain in DubUrl.

To plan virtual generated columns, qualify `PgsqlDialect` with `WithServerVersion` for PostgreSQL 18 or later. The shared QA fixture uses the connected server version to verify deployment on 18 and renderer rejection on 17.

To use a deliberately configured disposable database without provisioning, run `dotnet test` directly and set the matching `DUBURL_*_QA_CONNECTION_STRING` variable. Discovery tests also require `DUBURL_POSTGRESQL_DISCOVERY_QA_CONNECTION_STRING` or `DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING`. The normal runner sets all required variables and fails on skips.

The Oracle structured-type fixtures use the pinned Oracle Free image, including its vector and JSON modifier catalog fields. The disposable container provisions a default tablespace with automatic segment space management when the image's default lacks it.


## Mandatory shared provider tests

Every provider fixture inherits ProviderContract (which extends SchemaContract) from the internal DubUrl.Schema.QA helper. All seven providers must implement the abstract connection, dialect, script execution, error-code, qualification, comments, column/index catalog, object-existence, metadata-reader and public-deployment hooks. A missing override is a compilation error. Shared code owns the assertions; provider code returns typed observations.

`shared-scenarios.json` is the authoritative list of required scenarios. The NUnit case source embeds that same manifest; each provider declares an explicit expectation for every scenario and rejects missing or extra declarations. The QA runner validates both original baseline results and every Shared_<scenario> result passed exactly once. Missing, duplicate, skipped or failed cases fail CI. NoDiscovery can only apply to discovery/diagnostic scenarios, never to disable deployment, constraint or comment tests.

The scenarios cover deployment/replacement, quoted names, comments and empty descriptions, type/facet/null/default behavior, identities and both generated-column modes, single/composite keys and relationships, dependency order, checks/null semantics/regex, indexes and sort order, views, feature-specific discovery/filtering/closure, partial/strict metadata, round trips, native CHAR/VARCHAR keys, expression/filtered-index and aggregate-view diagnostics, public and fluent deployment, and cleanup after failure.

All original provider-specific regression fixtures remain, including SQL Server's qualified/unqualified same-batch extended-property comments with literal dots/apostrophes/Unicode/CRLF, SQLite's rich metadata diagnostic counts, Oracle's catalog check, DuckDB's public deployment/index tests, and MySQL's three inferred/native bounded-key/check/comment cases. MySQL's original feature cases run separately on the same disposable server and retain independent TRX validation.

## Explicit current limitations

A passing scenario means the declared behavior was verified, not that every engine implements the feature. Unsupported behavior is asserted without skips; each expectation includes its rationale and any required exception/error code. The expectations are written explicitly in each provider's Features.cs rather than inferred from the implementation under test.

| Provider | Current expectations verified by the shared tests |
|---|---|
| PostgreSQL | Portable virtual generated columns are rejected. |
| SQLite | Comments have no native storage and regex rendering is rejected. Default integer identities deploy through the public API; check/generated discovery and native bounded keys have positive coverage. Main/attached database identities, literal object names and supported discovery round trips are verified. String length is not enforced. |
| DuckDB | Stored generated columns and filtered indexes are rejected; declared string length and index sort direction are not retained. |
| SQL Server | Regex rendering is unavailable; expression indexes require computed columns. The adapter executes CREATE VIEW in its required separate batch, while comments stay together to detect batch-variable collisions. |
| Oracle | Managed Oracle discovery reads owner-qualified tables, columns, keys, comments, plain indexes, and simple projection views in the current PDB. Unscoped inspection excludes Oracle-maintained owners. Oracle NUMBER precision/scale and CHAR/BYTE length semantics are retained for replay. The baseline reader reports unsupported coverage for defaults, checks and generation. The version-qualified reader described below adds default discovery. Expression and descending indexes require additional catalog handling, and nonportable views produce a diagnostic. The runner creates disposable users to verify catalog visibility and cross-owner foreign-key closure. |
| MySQL | Native discovery verifies facets, keys, defaults, generation, checks, comments, BTREE indexes, views, database filtering, closure and strict/partial behavior. UNIQUE indexes are represented as constraints without duplicate backing indexes. Custom per-column identity settings and filtered indexes are rejected. |
| SingleStore | Comments, generated columns and supported discovery are verified. Key/index discovery explicitly selects shard-compatible rowstore layouts. Numeric BTREE direction is checked against native definitions. Foreign keys, table-level CHECK DDL, expression/filtered indexes, custom identities and virtual generation retain explicit rejection cases. |

Where discovery is absent, every discovery scenario verifies the real provider has no SchemaDiscoveryCapability and SchemaInspector returns SDI001. Where a reader exists, tests execute live DDL, read actual metadata, and assert values/coverage/diagnostic codes. This does not substitute fabricated metadata for a missing reader.

Shared_DiscoveryUnique verifies named single and composite UNIQUE constraints for all seven providers, including column order, complete strict inspection and no duplicated backing indexes.

## Explicit UNIQUE null policies

| Provider | Nullable tuples |
|---|---|
| SQL Server | Native UNIQUE treats matching null positions as equal. A unique index filtered to all-nonnull tuples implements `NullsDistinct` and cannot serve as a foreign-key target. Discovery recognizes that filter shape and maps it as one UNIQUE tuple. |
| DuckDB | Native UNIQUE allows duplicate tuples containing a null. DuckDB generates native constraint names, so a custom name uses a standalone unique index. Discovery preserves that index as an index and foreign keys require a native key. Nullable `NullsNotDistinct` is rejected before deployment. |
| PostgreSQL | Native UNIQUE allows duplicate tuples containing a null. `NullsNotDistinct` uses `NULLS NOT DISTINCT` on PostgreSQL 15 or later. Discovery reads the policy from the constraint's backing index. |
| MySQL | Native UNIQUE implements `NullsDistinct`, including for composite keys. Discovery reports the policy and ordered members without a second index. |
| SQLite | Native UNIQUE implements `NullsDistinct`. DDL uses the declared name and discovery retains SQLite's generated autoindex name for the constraint. |
| SingleStore | Native UNIQUE implements `NullsDistinct` on rowstore and columnstore. Every key must contain the nonempty shard key. Columnstore permits one primary or unique key. Foreign-key targets are rejected. Discovery reports the policy and key order. Replay also needs the physical layout supplied by the caller. |
| Oracle | Native composite UNIQUE compares partially-null tuples and allows duplicate all-null tuples. An explicit `NullsDistinct` plan works for a nullable single-column key, and `NullsNotDistinct` works when a required member rules out all-null tuples. Both explicit policies are rejected for a fully nullable composite key. Discovery retains that shape as Native and reports the equivalent explicit policies for a nullable single member or a composite with mixed nullability. The live round trip starts with handwritten Oracle DDL, including a separately named backing index, and verifies tuple behavior before and after replay. |

MySQL, SQLite, and SingleStore reject `NullsNotDistinct` when a key member can be null. A native UNIQUE constraint can enforce either explicit policy when all members are required.

`UniqueNullPolicyTests` exercises the plans on .NET 10 using real databases. The cases assert tuple order, catalog names, one enforcement structure, foreign-key eligibility, and inserts with both members present, either member null, and both members null.

## Typed column defaults

The Explicit DEFAULT NULL row uses provider unit tests that assert the generated CREATE TABLE includes
`DEFAULT NULL` only on the column that requests it. The live `DiscoveryNullDefault` scenario inspects
handwritten DDL with an explicit NULL, an omitted default, and a numeric default. The evidence
for unsupported inspection identifies whether the engine catalog or the DubUrl reader loses the distinction.

Typed defaults require DubUrl.Schema 0.103.0. The schema planner validates each expression and destination before sending DDL. A rejected mapping raises `ColumnDefaultPlanningException` with code `SDD001`. Engine-specific catalog readers recognize the SQL forms they can map back to typed defaults.

| Engine and route | CREATE TABLE planning | Catalog discovery and qualifications |
|---|---|---|
| PostgreSQL | Temporal defaults use the transaction clock. Context defaults use the inserting session. Qualified external sequences are supported. Native UUID generation requires `PgsqlDialect.WithServerVersion` with PostgreSQL 13 or later. | The PostgreSQL reader recognizes the supported forms and checks sequence dependencies without advancing a sequence. The TimescaleDB registration uses the PostgreSQL renderer and has its own live case. |
| SQL Server | Temporal, context and native GUID defaults are available. `SessionUser` returns the original server login, while `CurrentUser` returns the effective database user. Named sequences require `TSqlDialect.WithServerVersion` with major version 11 or later and must belong to the table's database. | The native reader recognizes the supported definitions and sequence references. |
| MySQL and MariaDB | `WithInnoDbDefaults` qualifies a MySQL session that creates InnoDB tables. Expression defaults start at 8.0.13 and user defaults at 8.0.34. `WithMariaDbDefaults` qualifies MariaDB independently from 10.2.1, with named sequences from 10.3. Both engines can return NULL for schema and database context when the session has no selected database. | Use `MySqlMetadataReader` or `MariaDbMetadataReader` for the matching catalog. MariaDB canonicalizes both context aliases to the database expression. MySQL binary UUIDs use network byte order. UUID version follows the engine. |
| SingleStore | `SingleStoreDialect.WithServerVersion` enables the DATETIME/TIMESTAMP default from 9.1.1. The other temporal defaults and UUID defaults on ordinary sharded tables are rejected. | The SingleStore reader recognizes the supported timestamp form. |
| DuckDB | Temporal defaults use the transaction clock. The identity expressions return the fixed compatibility value `duckdb`. Native and canonical-text UUIDs and schema-qualified external sequences are supported. DuckDB's sequence name parser cannot represent embedded double quotes. | The reader preserves external references and only reconstructs portable identity when the sequence has the generated name and exclusive dependency evidence. |
| SQLite | The SQLite clock yields UTC text with whole-second precision. UUID text sets the version and variant bits using SQLite's built-in pseudo-random functions. | The SQLite reader recognizes these text representations. |
| Oracle | The date default truncates to midnight. Database context returns Oracle's `DB_NAME`. `SYS_GUID()` yields the engine's 16 raw bytes. External sequences require `OracleDialect.WithServerVersion` with Oracle 12c or later. | Construct `OracleMetadataReader` with the server version to enable default discovery through `DATA_DEFAULT_VC` on Oracle 23 or later. |
| CockroachDB | Temporal, context, native UUID, and qualified sequence defaults use CockroachDB syntax. The explicit `pg_catalog.session_user()` call preserves the session user across `SET ROLE`. Sequence values can have gaps. | Live insertion is tested through the CockroachDB registration. |
| CrateDB | Temporal defaults use the native UTC clock. A date uses native `TIMESTAMP WITHOUT TIME ZONE` at midnight. Time uses native `BIGINT` microseconds since UTC midnight at millisecond resolution. Context names use unbounded text, and the catalog value is always `crate`. | Live insertion is tested separately from PostgreSQL. |
| Firebird 5 | `FirebirdSqlDialect.WithServerVersion` enables the native timestamp, date, time and user variables. The two user expressions return the same session identity. | The focused live case uses a disposable Firebird 5 database configured with `DUBURL_FIREBIRD_SCHEMA_QA_CONNECTION_STRING`. |
| Access | `MsAccessDialect.WithAceOleDbDefaults` enables timestamp, date, time and native GUID defaults. Dates and times use Access DATETIME conventions. The unqualified dialect used for ODBC rejects typed defaults. | The Windows QA case exercises both transports with a disposable copy of the Access file. |
| Db2 LUW | `WithLuwColumnDefaults` enables native temporal, user and current schema special registers from the LUW 11.5 baseline. | The live case runs against the disposable Db2 LUW fixture. |
| Teradata Vantage and Snowflake | `WithVantageColumnDefaults` and `WithStandardTableDefaults` opt into the documented engine forms. | `DocumentedColumnDefaultTests` checks offline rendering. These mappings have no live qualification in this change. |

`TemporalDefaultTests` and `TypedDefaultTests` cover insert-time evaluation, explicit values and NULL, supported discovery, session role and time-zone behavior, UUID representation, and external sequence ownership. External sequences are managed by the caller. The typed column default references an existing sequence and does not add a create or drop command for it.

## Framework and cleanup guarantees

The runner accepts only net10.0. A global NUnit setup guard also prevents every provider fixture, including retained regressions, from touching a database under another runtime. QA projects still compile with the multi-target solution; the CI database matrix contains .NET 10 only.

Each shared scenario opens its own connection, uses unique object names, records partially created objects, and verifies cleanup in finally. Embedded public-deployment tests use disposable files and close provider pools before deleting SQLite files. Container logs redact generated passwords and all provisioned containers/volumes are removed. MySQL readiness requires an authenticated TCP query against the initialized DubUrl database; the temporary socket-only bootstrap server cannot satisfy it.

The namespace, column-definition and table/key contracts are available in
published DubUrl 0.57.1. MySQL and SingleStore use their registered native readers
and database-qualified discovery filters. MySQL ordinary BTREE direction and
supported key/default/check/generation/comment/view metadata are checked
positively. SingleStore uses shard-compatible rowstore declarations for unique
keys and primary keys. Numeric BTREE direction is verified by SHOW CREATE TABLE
and the native reader, without treating character collations as direction flags.
Native UNIQUE indexes are read as constraints and tested separately from ordinary
indexes. Its foreign keys, table-level CHECK declarations, expression
indexes, predicate-filtered indexes and custom identity settings retain explicit
engine/renderer limitation assertions. Neither provider claims discovery is absent.

Use `-ResultsDirectory artifacts/results/<run-name>/<database>` to retain distinct
QA attempts without deleting earlier reports. Omitting it preserves the existing
results location and refusal to overwrite prior runs.
