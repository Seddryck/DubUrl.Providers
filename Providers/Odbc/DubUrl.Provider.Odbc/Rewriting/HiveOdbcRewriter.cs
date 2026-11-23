using System.Data.Common;
using DubUrl.Conversion;
using DubUrl.Locating.OdbcDriver;
using DubUrl.Parsing;
using DubUrl.Rewriting;
using DubUrl.Rewriting.Tokening;

namespace DubUrl.Provider.Odbc.Rewriting;

internal sealed class HiveOdbcRewriter(DbConnectionStringBuilder builder, DriverLocatorFactory drivers,
    IEnumerable<Type> locatorOptions)
    : OdbcRewriter(builder, [new HostTokenMapper("Host", "localhost"), new EndpointMapper(),
        new AuthentificationMapper(), new DriverMapper(drivers, locatorOptions), new HiveOptionsMapper()])
{
    private static ThriftOdbcTransport? FindTransport(SchemeKey scheme)
        => ThriftOdbcTransport.Find(scheme, "hive", "Hive");

    public override ConnectionUri Convert(SchemeKey scheme, DbConnectionStringBuilder source)
    {
        var parts = new ConnectionStringParts(source);
        parts.TakeHost("Host");
        parts.TakePort("Port");
        parts.TakeCredentials("UID", "PWD");
        if (FindTransport(scheme) is { } transport)
            transport.AddSegments(parts);
        else
        {
            var schema = parts.Take("Schema");
            if (schema.Length > 0)
                parts.Segments.Add(schema);
        }

        return parts.ToUri(scheme);
    }

    private sealed class HiveOptionsMapper : OptionsMapper
    {
        protected override bool ShouldMap(ConnectionUri url, string option)
            => !ThriftOdbcTransport.IsSasl(option) || FindTransport(url.Scheme) is null;
    }

    private sealed class EndpointMapper : BaseTokenMapper
    {
        public override void Execute(ConnectionUri urlInfo)
        {
            var transport = FindTransport(urlInfo.Scheme);
            Specificator.Execute("Port", urlInfo.Port > 0 ? urlInfo.Port : transport?.DefaultPort ?? 10000);
            Specificator.Execute("HiveServerType", "2");

            if (transport is not null)
                transport.Map(urlInfo, Specificator, "Hive");
            else
            {
                if (urlInfo.Segments.Count > 1)
                    throw new InvalidConnectionUrlTooManySegmentsException("Apache Hive", urlInfo.Segments);
                if (urlInfo.Segments.Count == 1 && urlInfo.Segments[0].Length > 0)
                    Specificator.Execute("Schema", urlInfo.Segments[0]);
            }
        }
    }
}
