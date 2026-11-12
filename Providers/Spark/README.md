# Spark SQL and Databricks SQL dialects

The `odbc+spark` scheme selects `SparkSqlDialect` and the `odbc+databricks` scheme selects `DatabricksSqlDialect`. An explicit `Driver` option changes which driver opens the connection. The scheme continues to select the dialect.

Both dialects quote each identifier component with backticks and double an embedded backtick. A dot in a supplied component is literal. The dialect leaves omitted qualifiers out of the SQL so the server uses the session defaults.

- **Spark SQL** defaults to the Spark 3.5.8 built-in `spark_catalog` rules. Table and namespace names accept ASCII letters, digits, and underscores, with case-insensitive lookup by default. To model a custom catalog or different session settings, use `WithCatalogRules` with the catalog's comparer, maximum namespace depth, and optional table and namespace validator. Set `isCurrent: true` to use these rules for names with no catalog qualifier. Pass multipart namespaces as separate elements to `RenderObjectName`. The portable schema model represents a single schema component. [Spark session catalog implementation](https://github.com/apache/spark/blob/v3.5.8/sql/catalyst/src/main/scala/org/apache/spark/sql/catalyst/catalog/SessionCatalog.scala)
- **Databricks SQL** uses Unity Catalog naming rules. Supply an object name, a schema and object, or a catalog, schema, and object. The renderer preserves the supplied spelling. Unity Catalog stores securable object names in lowercase and looks up columns case-insensitively while preserving their spelling. Backtick quoting allows names such as `qa-table`. Unity Catalog rejects dots, spaces, forward slashes, ASCII controls, and securable names longer than 255 characters. Storage formats such as Delta can impose further column-name restrictions. [Unity Catalog requirements](https://docs.databricks.com/aws/en/data-governance/unity-catalog/requirements)

Use `ValidateObjectNames` to check a batch of table identities for collisions under the selected catalog's rules. Supply the same qualification for names that resolve to the same namespace. Portable table planning also checks column collisions.

## Spark connection URLs

| Scheme | Thrift transport | TLS | Default port |
| --- | --- | --- | --- |
| `odbc+spark+http` | HTTP | Off | 10001 |
| `odbc+spark+https` | HTTP | On | 10001 |
| `odbc+spark+tcp` | TCP | Off | 10000 |
| `odbc+spark+tcps` | TCP | On | 10000 |

```text
odbc+spark+http://<host>[:<port>][/<http-path>][/schema/<schema>]
odbc+spark+https://<host>[:<port>][/<http-path>][/schema/<schema>]
odbc+spark+tcp://<host>[:<port>][/schema/<schema>]
odbc+spark+tcps://<host>[:<port>][/schema/<schema>]
```

HTTP and HTTPS use `ThriftTransport=2` and default to `/cliservice` when the endpoint path is omitted. A final `/schema/<name>` selects the session schema, so endpoints that end in that pair are unsupported by these schemes. Catalog selection uses Spark session configuration or SQL-qualified names.

On TCP and TCPS, `?sasl=0` selects raw binary Thrift and `?sasl=1` selects SASL. A compatible `ThriftTransport=0` or `ThriftTransport=1` also works. Omitting both uses the driver's default. Authentication and credentials use their existing settings.

The explicit schemes reject conflicting `SSL`, `ThriftTransport`, `HTTPPath`, or path-supplied `Schema` options. For existing `odbc+spark` URLs, the path continues to map to `HTTPPath` and driver options can select the transport and schema. [Spark Thrift Server defaults](https://spark.apache.org/docs/3.5.8/sql-distributed-sql-engine.html#running-the-thrift-jdbcodbc-server) · [Simba transport settings](https://documentation.insightsoftware.com/simba-spark-windows-driver-guide/content/odbc/hi/options/ui/thrifttransport.htm)

## Databricks connection URLs

```text
odbc+databricks://<host>[:<port>]/warehouse/<warehouse-id>[/<catalog>[/<schema>]]
odbc+databricks://<host>[:<port>]/cluster/<workspace-id>/<cluster-id>[/<catalog>[/<schema>]]
```

The warehouse form generates `HTTPPath=/sql/1.0/warehouses/<warehouse-id>`. The cluster form generates `HTTPPath=/sql/protocolv1/o/<workspace-id>/<cluster-id>`. Find the hostname and IDs in the compute resource's connection details. The optional trailing segments map to the driver's `Catalog` and `Schema` settings. Include a catalog when specifying a schema.

Use the shortcut path in the URL. The mapper rejects a raw HTTP endpoint path and the `HTTPPath`, `Catalog`, and `Schema` query options. Other driver options can still be passed in the query string. [Databricks ODBC connection details](https://docs.databricks.com/aws/en/integrations/odbc/compute)

## Databricks Delta schema generation

`ScriptSchema` exports the statements in deployment order, including Delta table properties and separate `ALTER TABLE` statements for enforced checks. Deploy a schema model or fluent builder to execute those commands on one connection. The shared schema model treats empty and whitespace-only descriptions as absent.

`WithTimeType()` enables `TIME` columns and `current_time()` defaults on compatible compute. Databricks treats `current_user()` and `session_user()` as aliases. Supply insert-session schema and catalog values in the insert itself. Databricks `current_database()` is an alias for `current_schema()`. Generate UUID values in the insert because Delta rejects the nondeterministic default. [TIME type](https://docs.databricks.com/aws/en/sql/language-manual/data-types/time-type) · [Session user](https://docs.databricks.com/aws/en/sql/language-manual/functions/session_user) · [Current database](https://docs.databricks.com/aws/en/sql/language-manual/functions/current_database)

Use `WithInformationalKeys()` to accept Unity Catalog's informational primary and foreign keys. Use `WithInformationalKeys(supportsUnique: true)` on compute that supports informational `UNIQUE` and foreign keys that reference it. Deployment checks the visible keys of existing parent tables before applying replacement commands. [Databricks key constraints](https://docs.databricks.com/aws/en/sql/language-manual/sql-ref-syntax-ddl-create-table-constraint) · [Unity Catalog metadata visibility](https://docs.databricks.com/aws/en/sql/language-manual/sql-ref-information-schema)
