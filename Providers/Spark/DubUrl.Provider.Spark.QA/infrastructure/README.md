# Apache Spark ODBC QA

This fixture starts the Spark 3.5.8 SQL Thrift Server in Docker Compose. It uses local mode and exposes binary Thrift with no authentication on host loopback port 10000. The Compose health check waits until a Beeline query succeeds. The test then opens a separate ODBC connection through DubUrl and checks `SELECT 1`. The DDL test creates a uniquely named schema in `spark_catalog`, creates a table named `select` with a quoted `SeLeCt` column, checks its name and type with `DESCRIBE TABLE`, and drops the table and schema. The Spark route defaults to standard Thrift results (`EnableArrow=0`) for the Apache Spark server. The runner removes the container, network, and volumes after the test. [Spark Thrift Server](https://spark.apache.org/docs/3.5.8/sql-distributed-sql-engine.html#running-the-thrift-jdbcodbc-server)

The live suite also runs the shared `OdbcContract` and query `ProviderContract` against five customer rows defined in each statement. It covers typed scalar reads, query templates, and the repository, Dapper, and DbReader paths. The pinned Simba driver leaves parameters unbound against this Spark Thrift Server in both native and translation modes. Spark 3.5.8 has no standalone `TIME` type, and `System.Data.Odbc` cannot decode the interval type exposed by this driver. The suite marks those cases inapplicable.

Use Debian or Ubuntu x64 with .NET 10, Docker Compose v2, unixODBC and `odbcinst`. Obtain the licensed Debian x64 **Simba Apache Spark ODBC Connector** from insightsoftware and install it and any required license on the runner. Register the driver in unixODBC as `Simba Spark ODBC Driver` with `Driver` set to the absolute path to the package-owned library. Verify the registration with `odbcinst -q -d -n 'Simba Spark ODBC Driver'`. [Simba Debian install guide](https://documentation.insightsoftware.com/simba-spark-windows-driver-guide/content/odbc/debian/installdeb.htm)

Spark 3.5.8 was qualified with the Debian x64 package `simbaspark_2.11.0.1009-2_amd64.deb`. Set `SPARK_ODBC_DRIVER_VERSION=2.11.0.1009-2` for that build. For another installer, use the full Debian package version from `dpkg-deb -f <installer.deb> Version`. The runner compares it with the package owning the registered library and records it with the test results. Run from the repository root.

```sh
bash Providers/Spark/DubUrl.Provider.Spark.QA/infrastructure/run-qa.sh
```

For GitHub Actions, register an approved Linux x64 runner with label `duburl-spark-qa` and add `SPARK_ODBC_DRIVER_VERSION` as a repository Actions variable. Dispatch **Spark and Databricks live ODBC QA** with fixture `spark`. Before the workflow exists on `main`, pushing a reviewed commit to `qa/odbc-connectivity` runs Spark QA when the version variable is configured.
