using DubUrl.Mapping;
using DubUrl.Provider.Spark.Sql;

namespace DubUrl.Provider.Spark.Metadata;

[Database<SparkSqlDialect>("Apache Spark", ["spark"], DatabaseCategory.DistributedQueryEngine)]
public sealed class SparkDatabase : IDatabase { }

[Database<DatabricksSqlDialect>("Databricks", ["databricks"], DatabaseCategory.DistributedQueryEngine)]
public sealed class DatabricksDatabase : IDatabase { }
