using System.Data.Common;
using DubUrl.Discovery;
using DubUrl.Mapping;
using DubUrl.Provider.Hive.Discovery;
using DubUrl.Provider.Odbc.Connectivity;
using DubUrl.Provider.Odbc.Drivers.Discovery;
using DubUrl.Provider.Odbc.Rewriting;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Parametrizing;

namespace DubUrl.Provider.Odbc.Mapping;

[WrapperMapper<OdbcConnectivity, PositionalParametrizer>("System.Data.Odbc")]
[SchemaDiscovery(typeof(HiveMetadataReader))]
public sealed class HiveOdbcMapper : BaseMapper, IOdbcMapper
{
    public HiveOdbcMapper(DbConnectionStringBuilder builder, IDialect dialect, IParametrizer parametrizer,
        IEnumerable<Type> locatorOptions)
        : base(new HiveOdbcRewriter(builder, new BuiltInDriverLocatorFactory(), locatorOptions), dialect, parametrizer)
    { }
}
