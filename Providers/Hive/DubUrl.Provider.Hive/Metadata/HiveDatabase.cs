using DubUrl.Mapping;
using DubUrl.Querying.Dialects;

namespace DubUrl.Provider.Hive.Metadata;

[Database<AnsiDialect>("Apache Hive", ["hive"], DatabaseCategory.DistributedQueryEngine)]
public sealed class HiveDatabase : IDatabase { }
