using DubUrl.Conversion;
using DubUrl.Parsing;
using DubUrl.Rewriting;
using DubUrl.Rewriting.Tokening;

namespace DubUrl.Provider.Odbc.Rewriting;

internal sealed record SparkOdbcTransport(string Suffix, bool IsHttp, bool UsesTls)
{
    private const string SchemePrefix = "odbc+spark+";
    private const string SchemaMarker = "schema";
    private const string DefaultHttpPath = "/cliservice";

    private static readonly SparkOdbcTransport[] Transports =
    [
        new("http", IsHttp: true, UsesTls: false),
        new("https", IsHttp: true, UsesTls: true),
        new("tcp", IsHttp: false, UsesTls: false),
        new("tcps", IsHttp: false, UsesTls: true),
    ];

    private static readonly HashSet<string> ManagedOptions =
        new(["SSL", "ThriftTransport", "HTTPPath", "Schema", "sasl"], StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<string> Aliases => Transports.Select(transport => SchemePrefix + transport.Suffix);
    internal int DefaultPort => IsHttp ? 10001 : 10000;

    internal static SparkOdbcTransport? Find(SchemeKey scheme)
    {
        if (!scheme.Parts.Contains("spark", StringComparer.OrdinalIgnoreCase))
            return null;

        var transport = Transports.FirstOrDefault(candidate =>
            scheme.Parts.Contains(candidate.Suffix, StringComparer.OrdinalIgnoreCase));
        if (transport is not null && scheme.Canonical != SchemePrefix + transport.Suffix)
            throw new InvalidConnectionUrlException("Specify one Spark transport suffix immediately after odbc+spark.");
        return transport;
    }

    internal void Map(ConnectionUri url, ISpecificator settings)
    {
        foreach (var option in url.Options.Keys)
            if (option != option.Trim() && ManagedOptions.Contains(option.Trim()))
                throw Conflict(option.Trim());

        var segments = url.Segments;
        var hasSchema = HasSchemaSuffix(segments);
        var schema = hasSchema ? segments[^1] : null;
        if (hasSchema && string.IsNullOrWhiteSpace(schema))
            throw new InvalidConnectionUrlException("The Spark schema segment must contain a schema name.");

        var endpointLength = segments.Count - (hasSchema ? 2 : 0);
        if (!IsHttp && endpointLength != 0)
            throw new InvalidConnectionUrlException("Spark TCP URLs accept only an optional /schema/<name> path.");

        if (endpointLength >= 2 && segments[endpointLength - 2] == SchemaMarker)
            throw new InvalidConnectionUrlException("Spark HTTP endpoints ending in /schema/<name> are unsupported.");

        var httpPath = endpointLength == 0 ? DefaultHttpPath : "/" + string.Join("/", segments.Take(endpointLength));
        var ssl = UsesTls ? "1" : "0";
        var thriftTransport = IsHttp ? "2" : null;

        if (url.Options.TryGetValue("sasl", out var sasl))
        {
            if (IsHttp || sasl is not ("0" or "1"))
                throw new InvalidConnectionUrlException("The Spark sasl option accepts 0 or 1 on TCP and TCPS URLs.");
            thriftTransport = sasl;
        }

        if (url.Options.TryGetValue("ThriftTransport", out var thriftOption)
            && (thriftTransport is not null ? thriftOption != thriftTransport : thriftOption is not ("0" or "1")))
            throw Conflict("ThriftTransport");

        RequireCompatible(url, "SSL", ssl);
        RequireCompatible(url, "HTTPPath", IsHttp ? httpPath : null);
        if (schema is not null)
            RequireCompatible(url, "Schema", schema);

        settings.Execute("SSL", ssl);
        if (thriftTransport is not null)
            settings.Execute("ThriftTransport", thriftTransport);
        if (IsHttp)
            settings.Execute("HTTPPath", httpPath);
        if (schema is not null)
            settings.Execute("Schema", schema);
    }

    internal void AddSegments(ConnectionStringParts parts)
    {
        var httpPath = parts.Take("HTTPPath");
        if (IsHttp)
        {
            if (!httpPath.StartsWith('/') || httpPath == "/")
                throw new NotSupportedException();

            var endpoint = httpPath[1..].Split('/');
            if (HasSchemaSuffix(endpoint))
                throw new NotSupportedException();

            if (httpPath != DefaultHttpPath)
                parts.Segments.AddRange(endpoint);
        }
        else if (httpPath.Length > 0)
            throw new NotSupportedException();

        var schema = parts.Take("Schema");
        if (schema.Length > 0)
            parts.Segments.AddRange([SchemaMarker, schema]);

        var ssl = parts.Take("SSL");
        if (ssl != (UsesTls ? "1" : "0"))
            throw new NotSupportedException();

        var thrift = parts.Take("ThriftTransport");
        if (IsHttp && thrift != "2" || !IsHttp && thrift is not ("" or "0" or "1"))
            throw new NotSupportedException();

        if (!IsHttp && thrift.Length > 0)
            parts.Options["sasl"] = thrift;
    }

    internal static bool IsSasl(string option) => option.Equals("sasl", StringComparison.OrdinalIgnoreCase);

    private static bool HasSchemaSuffix(IReadOnlyList<string> segments)
        => segments.Count >= 2 && segments[^2] == SchemaMarker;

    private static void RequireCompatible(ConnectionUri url, string option, string? expected)
    {
        if (url.Options.TryGetValue(option, out var value) && value != expected)
            throw Conflict(option);
    }

    private static InvalidConnectionUrlException Conflict(string option)
        => new($"The Spark URL option '{option}' conflicts with the transport or path.");
}
