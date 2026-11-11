# Apache Hive ODBC QA

The suite opens `odbc+hive://` through DubUrl and checks `SELECT 1` against Apache HiveServer2 4.0.1 and Spark SQL 3.5.8. Both fixtures use binary Thrift without authentication on dynamically assigned loopback ports. Docker Compose waits for Beeline to query each server before the ODBC tests run. The Spark service comes from the existing Spark QA fixture.

Use Linux x64 with .NET 10, Docker Compose v2, unixODBC, `odbcinst`, `rpm2cpio`, `cpio`, and the SASL library `libsasl2.so.2`. Obtain [Cloudera Hive ODBC 2.5.0.1001 for EL6 x64](https://downloads.cloudera.com/connectors/hive-2.5.0.1001/Linux/EL6/ClouderaHiveODBC-2.5.0.1001-1.el6.x86_64.rpm). The runner checks its SHA-256, extracts it to a private directory, and configures unixODBC for the test process. It cleans up the driver directory and Compose fixtures afterward.

Run from the repository root.

```bash
HIVE_ODBC_RPM=/absolute/path/to/ClouderaHiveODBC-2.5.0.1001-1.el6.x86_64.rpm \
  bash Providers/Hive/DubUrl.Provider.Hive.QA/infrastructure/run-qa.sh
```

The driver version and test results are written to `.test-results/hive/`. The **Hive ODBC QA** workflow runs the same suite.
