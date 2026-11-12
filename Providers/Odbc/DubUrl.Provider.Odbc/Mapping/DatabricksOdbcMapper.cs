using System.Data.Common;
using DubUrl.BulkCopy;
using DubUrl.Mapping;
using DubUrl.Provider.Odbc.Connectivity;
using DubUrl.Provider.Odbc.Drivers.Discovery;
using DubUrl.Provider.Odbc.Rewriting;
using DubUrl.Provider.Odbc.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Parametrizing;

namespace DubUrl.Provider.Odbc.Mapping;

[WrapperMapper<OdbcConnectivity, PositionalParametrizer>("System.Data.Odbc")]
[FileLoad(typeof(DatabricksFileLoader))]
public sealed class DatabricksOdbcMapper : BaseMapper, IOdbcMapper
{
    public DatabricksOdbcMapper(DbConnectionStringBuilder builder, IDialect dialect, IParametrizer parametrizer,
        IEnumerable<Type> locatorOptions)
        : base(new SparkOdbcRewriter(builder, new BuiltInDriverLocatorFactory(), locatorOptions), dialect, parametrizer)
    { }
}
