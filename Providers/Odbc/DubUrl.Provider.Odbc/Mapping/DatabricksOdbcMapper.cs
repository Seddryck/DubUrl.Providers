using System.Data.Common;
using DubUrl.BulkCopy;
using DubUrl.Discovery;
using DubUrl.Mapping;
using DubUrl.Provider.Odbc.Connectivity;
using DubUrl.Provider.Odbc.Sql;
using DubUrl.Provider.Spark.Discovery;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Parametrizing;

namespace DubUrl.Provider.Odbc.Mapping;

[WrapperMapper<OdbcConnectivity, PositionalParametrizer>("System.Data.Odbc")]
[FileLoad(typeof(DatabricksFileLoader))]
[SchemaDiscovery(typeof(DatabricksMetadataReader))]
public sealed class DatabricksOdbcMapper : SparkOdbcMapper
{
    public DatabricksOdbcMapper(DbConnectionStringBuilder builder, IDialect dialect, IParametrizer parametrizer,
        IEnumerable<Type> locatorOptions) : base(builder, dialect, parametrizer, locatorOptions) { }
}
