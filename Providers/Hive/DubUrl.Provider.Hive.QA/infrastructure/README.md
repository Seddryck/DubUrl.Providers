# Apache Hive ODBC QA

The suite opens `odbc+hive://` through DubUrl and checks `SELECT 1` against Apache HiveServer2 4.0.1. The baseline fixture uses binary Thrift without authentication on a dynamically assigned loopback port. The suite also runs a query rendered by DubUrl with a backtick-quoted reserved identifier and a qualified column reference.

Use Linux x64 with .NET 10, Docker Compose v2, OpenSSL, unixODBC, `odbcinst`, `dpkg-deb`, and `libsasl2.so.2`. Download the [Cloudera Hive ODBC 2.10.1.1001-2 Debian x64 package](https://downloads.cloudera.com/connectors/ClouderaHiveODBC-2.10.1.1001/Debian/clouderahiveodbc_2.10.1.1001-2_amd64.deb). The runner verifies the SHA-256 checksum, configures unixODBC in a private directory, and cleans up the directory and Compose fixtures afterward.

Run from the repository root.

```bash
HIVE_ODBC_DEB=/absolute/path/to/clouderahiveodbc_2.10.1.1001-2_amd64.deb \
  bash Providers/Hive/DubUrl.Provider.Hive.QA/infrastructure/run-qa.sh
```

The driver version and test results are written to `.test-results/hive/`. The **Hive ODBC QA** workflow runs the same suite.

## Transport coverage

The runner also starts HiveServer2 in HTTP, HTTPS, and TLS-wrapped TCP modes. It creates a temporary certificate for `localhost` and configures Beeline to trust it for the two TLS controls. Each control must return `1` from `SELECT 1` before ODBC tests run.

All four schemes run `SELECT 1` through DubUrl with Cloudera Hive ODBC 2.10.1.1001. TCP and TCPS use raw binary Thrift with `sasl=0&AuthMech=0`. HTTP and HTTPS use the default `/cliservice` endpoint and `AuthMech=3` to send a Basic Authorization header. The HTTP fixture accepts a generated placeholder password. The HTTPS and TCPS tests pass the fixture certificate through `TrustedCerts` and connect as `localhost`.

## Shared contracts

After readiness, the runner creates a five-row `Customer` view in HiveServer2. The shared ODBC and query contracts use it to exercise connection reopen, positional parameter binding, scalar and typed results, templates, and repository reads, including Dapper and DbReader. The pinned Cloudera driver binds positional parameters in SQL translation mode (`UseNativeQuery=0`). With native-query mode, HiveServer2 reports an unresolved `$1` during compilation even though the test binds the value through ODBC. The smoke tests and the HiveQL quoting test use native-query mode (`UseNativeQuery=1`).

The MicroOrm queries use explicit aliases that preserve property-name casing in SQL translation mode. With the newer driver, both the youngest-customers query and the generated `DATE` predicate pass. HiveQL converts the newer driver’s `DateTime` results to `DateOnly` and still accepts date strings returned by older drivers.

### Unsupported checks

- Named parameter markers are unavailable in ODBC. The contracts exercise positional `?` binding.
- HiveServer2 4.0.1 has no standalone `TIME` type. Hive interval literals and the interval text returned by the pinned driver also differ from DubUrl's shared `TimeSpan` formatter and conversion.
- The fixture uses unauthenticated binary Thrift and has no live password, OS identity, token, or client-certificate login recipe. Inherited authentication tests report those prerequisite skips and run the applicable unsupported-strategy and credential-conflict checks.
- Hive has no DubUrl schema discovery or bulk-copy adapter. The inherited tests verify the unsupported diagnostics for both.
