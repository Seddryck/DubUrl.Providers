using DubUrl.Mapping;
using DubUrl.Provider.Spark.Sql;

namespace DubUrl.Provider.Spark.Metadata;

[Database<SparkSqlDialect>("Apache Spark", ["spark"], DatabaseCategory.DistributedQueryEngine)]
[Brand("apachespark", "#E25A1C")]
public sealed class SparkDatabase : IDatabase { }

[Database<DatabricksSqlDialect>("Databricks", ["databricks"], DatabaseCategory.DistributedQueryEngine)]
[Brand("databricks", "#FF3621")]
public sealed class DatabricksDatabase : IDatabase { }
