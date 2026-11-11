using System.Data.Common;
using DubUrl.Conversion;
using DubUrl.Locating.OdbcDriver;
using DubUrl.Parsing;
using DubUrl.Rewriting;
using DubUrl.Rewriting.Tokening;

namespace DubUrl.Provider.Odbc.Rewriting;

internal sealed class HiveOdbcRewriter(DbConnectionStringBuilder builder, DriverLocatorFactory drivers,
    IEnumerable<Type> locatorOptions)
    : OdbcRewriter(builder, [new HostTokenMapper("Host", "localhost"), new PortTokenMapper("Port", 10000),
        new SchemaMapper(), new AuthentificationMapper(), new DriverMapper(drivers, locatorOptions),
        new OptionsMapper()])
{
    public override ConnectionUri Convert(SchemeKey scheme, DbConnectionStringBuilder source)
    {
        var parts = new ConnectionStringParts(source);
        parts.TakeHost("Host");
        parts.TakePort("Port");
        parts.TakeCredentials("UID", "PWD");
        var schema = parts.Take("Schema");
        if (schema.Length > 0)
            parts.Segments.Add(schema);
        return parts.ToUri(scheme);
    }

    private sealed class SchemaMapper : BaseTokenMapper
    {
        public override void Execute(ConnectionUri urlInfo)
        {
            if (urlInfo.Segments.Count > 1)
                throw new InvalidConnectionUrlTooManySegmentsException("Apache Hive", urlInfo.Segments);
            if (urlInfo.Segments.Count == 1 && urlInfo.Segments[0].Length > 0)
                Specificator.Execute("Schema", urlInfo.Segments[0]);

            Specificator.Execute("HiveServerType", "2");
        }
    }
}
