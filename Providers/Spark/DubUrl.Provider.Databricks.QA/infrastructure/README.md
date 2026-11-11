# Databricks Free Edition ODBC QA

Use the `2X-Small` serverless SQL warehouse included in Databricks Free Edition. The connectivity test opens it through `odbc+databricks` and checks `SELECT 1`. The DDL test creates a uniquely named schema in the current Unity Catalog catalog, creates a table named `qa-table` with a quoted `SeLeCt` column, checks its name and type with `DESCRIBE TABLE`, and drops the table and schema. Use a workspace for non-commercial QA and dispatch the fixture on demand to stay within the Free Edition quota. [Free Edition limits](https://docs.databricks.com/aws/en/getting-started/free-edition-limitations)

1. In Databricks, open **SQL Warehouses**, select the included warehouse, and open **Connection details**. Copy **Server hostname** and the full **HTTP path** beginning with `/sql/1.0/warehouses/`. The PAT owner needs `CAN USE` on the warehouse and `USE CATALOG` and `CREATE SCHEMA` on the current workspace catalog. The schema creator owns the scratch schema and can create and drop its table. A connection can start a stopped warehouse. [ODBC warehouse settings](https://docs.databricks.com/aws/en/integrations/odbc/compute)
2. Under **Settings → Developer → Access tokens**, generate a short-lived workspace user PAT. Use the existing DubUrl username/password API to pass it to the driver as the password for the literal username `token`. [Databricks PAT setup](https://docs.databricks.com/aws/en/dev-tools/auth/pat#create-personal-access-tokens-for-workspace-users)
3. Under the repository's **Settings → Environments**, create `databricks-qa`. Allow deployments from `main` and `qa/odbc-connectivity` only. Add environment variables `DATABRICKS_HOST` and `DATABRICKS_HTTP_PATH`, and the environment secret `DATABRICKS_TOKEN`. Copy the token directly into the secret field.
4. Dispatch **Spark and Databricks live ODBC QA** with fixture `databricks`. Before the workflow exists on `main`, a maintainer can push the reviewed commit to `qa/odbc-connectivity` to trigger it. The GitHub-hosted Linux runner installs the pinned Databricks ODBC Driver 2.14.0 and unixODBC.

For a local Linux x64 run, install unixODBC and the vendor's [Databricks ODBC Driver 2.14.0 Debian x64 package](https://www.databricks.com/spark/odbc-drivers-download). Register the following section in the driver manager's `odbcinst.ini` and verify it with `odbcinst -q -d -n 'Databricks ODBC Driver'`.

```ini
[Databricks ODBC Driver]
Driver=/opt/databricks/databricksodbc/lib/64/libdatabricksodbc64.so
```

Set the same three environment values locally, reading the PAT from your secret store or a hidden prompt. Run from the repository root.

```sh
dotnet test Providers/Spark/DubUrl.Provider.Databricks.QA -c Release -f net10.0
```
