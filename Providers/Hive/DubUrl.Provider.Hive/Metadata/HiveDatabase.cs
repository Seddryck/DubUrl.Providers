using DubUrl.Mapping;
using DubUrl.Provider.Hive.Sql;

namespace DubUrl.Provider.Hive.Metadata;

[Database<HiveDialect>("Apache Hive", ["hive"], DatabaseCategory.DistributedQueryEngine)]
public sealed class HiveDatabase : IDatabase { }
