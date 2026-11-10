using System.Data.Common;
using DubUrl.Conversion;
using DubUrl.Locating.OdbcDriver;
using DubUrl.Parsing;
using DubUrl.Rewriting.Tokening;

namespace DubUrl.Provider.Odbc.Rewriting;

internal sealed class SparkOdbcRewriter(DbConnectionStringBuilder builder, DriverLocatorFactory drivers,
    IEnumerable<Type> locatorOptions)
    : OdbcRewriter(builder, [new EndpointMapper(), new AuthentificationMapper(),
        new DriverMapper(drivers, locatorOptions), new OptionsMapper()])
{
    public override ConnectionUri Convert(SchemeKey scheme, DbConnectionStringBuilder source)
    {
        var parts = new ConnectionStringParts(source);
        parts.TakeHost("Host");
        parts.TakePort("Port");
        parts.TakeCredentials("UID", "PWD");
        return parts.ToUri(scheme);
    }

    private sealed class EndpointMapper : BaseTokenMapper
    {
        public override void Execute(ConnectionUri urlInfo)
        {
            var databricks = urlInfo.Scheme.Parts.Contains("databricks", StringComparer.OrdinalIgnoreCase);
            Specificator.Execute("Host", string.IsNullOrEmpty(urlInfo.Host) ? "localhost" : urlInfo.Host);
            Specificator.Execute("Port", urlInfo.Port > 0 ? urlInfo.Port : databricks ? 443 : 10000);
            if (urlInfo.Segments.Count > 0)
                Specificator.Execute("HTTPPath", "/" + string.Join("/", urlInfo.Segments));

            if (databricks)
            {
                Specificator.Execute("ThriftTransport", "2");
                Specificator.Execute("SSL", "1");
            }
        }
    }
}
