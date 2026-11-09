# Real-database schema QA

```powershell
./QA/run.ps1 -Database Sqlite -Framework net10.0
./QA/run.ps1 -Database DuckDb -Framework net10.0
./QA/run.ps1 -Database PostgreSql -Framework net10.0
```

The runner builds only the selected fixture and framework, provisions a disposable database on a free local port, waits for readiness, requires nonzero passing tests with no skips, saves redacted logs and TRX reports, and removes its own container/volumes in `finally`. It never connects to an existing database. Remove old `artifacts/results/<Database>/<Framework>` output before repeating a run. PostgreSQL, SQL Server, Oracle, SingleStore and MySQL require Docker; SQLite and DuckDB run their actual embedded engines. Oracle and SingleStore also retain the original pinned images and startup readiness support. PostgreSQL retains its original pinned image. SQL Server uses an explicit CU image.

CI runs all seven database jobs on .NET 10 only. The unit matrix still covers .NET 8, 9 and 10; its embedded DuckDB integration cases run only in the .NET 10 entry. PostgreSQL/SQL Server/Oracle/SingleStore/MySQL use Linux; SQLite/DuckDB retain Windows. SchemaContract and DiscoveryContract are shared internally through `DubUrl.Schema.QA`, which is not published as a NuGet package. Provider connection, raw driver metadata and application-level integration tests remain in DubUrl.

To use a deliberately configured disposable database without provisioning, run `dotnet test` directly and set the matching `DUBURL_*_QA_CONNECTION_STRING` variable. Discovery tests also require `DUBURL_POSTGRESQL_DISCOVERY_QA_CONNECTION_STRING` or `DUBURL_SQLSERVER_DISCOVERY_QA_CONNECTION_STRING`. The normal runner sets all required variables and fails on skips.


## Mandatory shared provider tests

Every provider fixture inherits ProviderContract (which extends SchemaContract) from the internal DubUrl.Schema.QA helper. All seven providers must implement the abstract connection, dialect, script execution, error-code, qualification, comments, column/index catalog, object-existence, metadata-reader and public-deployment hooks. A missing override is a compilation error. Shared code owns the assertions; provider code returns typed observations.

QA/shared-scenarios.json is the authoritative list of 60 required scenarios. The NUnit case source embeds that same manifest; each provider declares an explicit expectation for every scenario and rejects missing or extra declarations. The QA runner validates both original baseline results and every Shared_<scenario> result passed exactly once. Missing, duplicate, skipped or failed cases fail CI. NoDiscovery can only apply to discovery/diagnostic scenarios, never to disable deployment, constraint or comment tests.

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
| Oracle | Managed Oracle discovery reads owner-qualified tables, columns, keys, comments, plain indexes, and simple projection views in the current PDB. Unscoped inspection excludes Oracle-maintained owners. Oracle NUMBER precision/scale and CHAR/BYTE length semantics are retained for replay. Defaults, checks and generation report unsupported coverage. Expression and descending indexes require additional catalog handling, and nonportable views produce a diagnostic. The runner creates disposable users to verify catalog visibility and cross-owner foreign-key closure. |
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
