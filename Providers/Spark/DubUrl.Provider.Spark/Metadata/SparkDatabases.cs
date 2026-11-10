using DubUrl.Mapping;
using DubUrl.Querying.Dialects;

namespace DubUrl.Provider.Spark.Metadata;

[Database<AnsiDialect>("Apache Spark", ["spark"], DatabaseCategory.DistributedQueryEngine)]
public sealed class SparkDatabase : IDatabase { }

[Database<AnsiDialect>("Databricks", ["databricks"], DatabaseCategory.DistributedQueryEngine)]
public sealed class DatabricksDatabase : IDatabase { }
