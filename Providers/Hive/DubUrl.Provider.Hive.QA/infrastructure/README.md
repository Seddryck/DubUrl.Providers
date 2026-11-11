# Apache Hive ODBC QA

The suite opens `odbc+hive://` through DubUrl and checks `SELECT 1` against Apache HiveServer2 4.0.1 and Spark SQL 3.5.8. Both fixtures use binary Thrift without authentication on dynamically assigned loopback ports. Docker Compose waits for Beeline to query each server before the ODBC tests run. The HiveServer2 test also runs a query rendered by DubUrl with a backtick-quoted reserved identifier and a qualified column reference. The Spark service comes from the existing Spark QA fixture.

Use Linux x64 with .NET 10, Docker Compose v2, unixODBC, `odbcinst`, `rpm2cpio`, `cpio`, and the SASL library `libsasl2.so.2`. Obtain [Cloudera Hive ODBC 2.5.0.1001 for EL6 x64](https://downloads.cloudera.com/connectors/hive-2.5.0.1001/Linux/EL6/ClouderaHiveODBC-2.5.0.1001-1.el6.x86_64.rpm). The runner checks its SHA-256, extracts it to a private directory, and configures unixODBC for the test process. It cleans up the driver directory and Compose fixtures afterward.

Run from the repository root.

```bash
HIVE_ODBC_RPM=/absolute/path/to/ClouderaHiveODBC-2.5.0.1001-1.el6.x86_64.rpm \
  bash Providers/Hive/DubUrl.Provider.Hive.QA/infrastructure/run-qa.sh
```

The driver version and test results are written to `.test-results/hive/`. The **Hive ODBC QA** workflow runs the same suite.

## Shared contracts

After readiness, the runner creates a five-row `Customer` view in HiveServer2. The shared ODBC and query contracts use it to exercise connection reopen, positional parameter binding, scalar and typed results, templates, and repository reads, including Dapper and DbReader. The pinned Cloudera driver binds positional parameters in SQL translation mode (`UseNativeQuery=0`). With native-query mode, HiveServer2 reports an unresolved `$1` during compilation even though the test binds the value through ODBC. The two smoke tests and the HiveQL quoting test use native-query mode (`UseNativeQuery=1`).

### Unsupported checks

- Named parameter markers are unavailable in ODBC. The contracts exercise positional `?` binding.
- HiveServer2 4.0.1 has no standalone `TIME` type. Hive interval literals and the interval text returned by the pinned driver also differ from DubUrl's shared `TimeSpan` formatter and conversion.
- The MicroOrm youngest-customers query uses explicit aliases that preserve property-name casing in SQL translation mode. When the shared MicroOrm predicate template emits a `DATE` literal, the legacy driver returns lowercase labels. MicroOrm binds property names by case, so that predicate test is skipped. The standalone HiveQL template test, Dapper, and DbReader still run.
- The fixture uses unauthenticated binary Thrift and has no live password, OS identity, token, or client-certificate login recipe. Inherited authentication tests report those prerequisite skips and run the applicable unsupported-strategy and credential-conflict checks.
- Hive has no DubUrl schema discovery or bulk-copy adapter. The inherited tests verify the unsupported diagnostics for both.
