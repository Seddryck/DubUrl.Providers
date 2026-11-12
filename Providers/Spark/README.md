# Spark SQL and Databricks SQL dialects

The `odbc+spark` scheme selects `SparkSqlDialect` and the `odbc+databricks` scheme selects `DatabricksSqlDialect`. An explicit `Driver` option changes which driver opens the connection. The scheme continues to select the dialect.

Both dialects quote each identifier component with backticks and double an embedded backtick. A dot in a supplied component is literal. The dialect leaves omitted qualifiers out of the SQL so the server uses the session defaults.

- **Spark SQL** defaults to the Spark 3.5.8 built-in `spark_catalog` rules. Table and namespace names accept ASCII letters, digits, and underscores, with case-insensitive lookup by default. To model a custom catalog or different session settings, use `WithCatalogRules` with the catalog's comparer, maximum namespace depth, and optional table and namespace validator. Set `isCurrent: true` to use these rules for names with no catalog qualifier. Pass multipart namespaces as separate elements to `RenderObjectName`. The portable schema model represents a single schema component. [Spark session catalog implementation](https://github.com/apache/spark/blob/v3.5.8/sql/catalyst/src/main/scala/org/apache/spark/sql/catalyst/catalog/SessionCatalog.scala)
- **Databricks SQL** uses Unity Catalog naming rules. Supply an object name, a schema and object, or a catalog, schema, and object. The renderer preserves the supplied spelling. Unity Catalog stores securable object names in lowercase and looks up columns case-insensitively while preserving their spelling. Backtick quoting allows names such as `qa-table`. Unity Catalog rejects dots, spaces, forward slashes, ASCII controls, and securable names longer than 255 characters. Storage formats such as Delta can impose further column-name restrictions. [Unity Catalog requirements](https://docs.databricks.com/aws/en/data-governance/unity-catalog/requirements)

Use `ValidateObjectNames` to check a batch of table identities for collisions under the selected catalog's rules. Supply the same qualification for names that resolve to the same namespace. Portable table planning also checks column collisions.

The Databricks ODBC route accepts `Catalog` and `Schema` as separate connection options to select session defaults.
