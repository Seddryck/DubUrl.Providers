using System.Data.Common;
using DubUrl.Conversion;
using DubUrl.Locating.OdbcDriver;
using DubUrl.Parsing;
using DubUrl.Rewriting;
using DubUrl.Rewriting.Tokening;

namespace DubUrl.Provider.Odbc.Rewriting;

internal sealed class SparkOdbcRewriter(DbConnectionStringBuilder builder, DriverLocatorFactory drivers,
    IEnumerable<Type> locatorOptions)
    : OdbcRewriter(builder, [new EndpointMapper(), new AuthentificationMapper(),
        new DriverMapper(drivers, locatorOptions), new SparkOptionsMapper()])
{
    public override ConnectionUri Convert(SchemeKey scheme, DbConnectionStringBuilder source)
    {
        var parts = new ConnectionStringParts(source);
        parts.TakeHost("Host");
        parts.TakePort("Port");
        parts.TakeCredentials("UID", "PWD");
        if (IsDatabricks(scheme))
            AddDatabricksSegments(parts);
        else
            SparkOdbcTransport.Find(scheme)?.AddSegments(parts);
        return parts.ToUri(scheme);
    }

    private static bool IsDatabricks(SchemeKey scheme)
        => scheme.Parts.Contains("databricks", StringComparer.OrdinalIgnoreCase);

    private static void AddDatabricksSegments(ConnectionStringParts parts)
    {
        string[] endpoint = parts.Take("HTTPPath").Split('/') switch
        {
            ["", "sql", "1.0", "warehouses", var id] => ["warehouse", id],
            ["", "sql", "protocolv1", "o", var workspace, var cluster] => ["cluster", workspace, cluster],
            _ => throw new NotSupportedException(),
        };

        var catalog = parts.Take("Catalog");
        var schema = parts.Take("Schema");
        if (catalog.Length == 0 && schema.Length != 0)
            throw new NotSupportedException();

        parts.Segments.AddRange(endpoint);
        if (catalog.Length > 0)
            parts.Segments.Add(catalog);
        if (schema.Length > 0)
            parts.Segments.Add(schema);
    }

    private sealed class SparkOptionsMapper : OptionsMapper
    {
        protected override bool ShouldMap(ConnectionUri url, string option)
            => !SparkOdbcTransport.IsSasl(option) || SparkOdbcTransport.Find(url.Scheme) is null;
    }

    private sealed class EndpointMapper : BaseTokenMapper
    {
        private static readonly HashSet<string> DatabricksPathSettings =
            new(["HTTPPath", "Catalog", "Schema"], StringComparer.OrdinalIgnoreCase);

        public override void Execute(ConnectionUri urlInfo)
        {
            var databricks = IsDatabricks(urlInfo.Scheme);
            var sparkTransport = SparkOdbcTransport.Find(urlInfo.Scheme);
            Specificator.Execute("Host", string.IsNullOrEmpty(urlInfo.Host) ? "localhost" : urlInfo.Host);
            Specificator.Execute("Port", urlInfo.Port > 0 ? urlInfo.Port : databricks ? 443 : sparkTransport?.DefaultPort ?? 10000);

            if (databricks)
            {
                MapDatabricksPath(urlInfo);
                Specificator.Execute("ThriftTransport", "2");
                Specificator.Execute("SSL", "1");
            }
            else
            {
                if (sparkTransport is not null)
                    sparkTransport.Map(urlInfo, Specificator);
                else if (urlInfo.Segments.Count > 0)
                    Specificator.Execute("HTTPPath", "/" + string.Join("/", urlInfo.Segments));

                // Apache Spark Thrift Server returns standard Thrift results.
                Specificator.Execute("EnableArrow", "0");
            }
        }

        private void MapDatabricksPath(ConnectionUri urlInfo)
        {
            foreach (var option in urlInfo.Options.Keys)
                if (DatabricksPathSettings.Contains(option.Trim()))
                    throw new InvalidConnectionUrlException("Specify the Databricks endpoint, catalog and schema in the URL path.");

            var segments = urlInfo.Segments;
            var (httpPath, endpointLength) = segments switch
            {
                ["warehouse", var id, ..] when id.Length > 0 && id.All(char.IsAsciiLetterOrDigit)
                    => ($"/sql/1.0/warehouses/{id}", 2),
                ["cluster", var workspace, var cluster, ..] when workspace.Length > 0 && workspace.All(char.IsAsciiDigit)
                    && cluster.Any(char.IsAsciiLetterOrDigit)
                    && cluster.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
                    => ($"/sql/protocolv1/o/{workspace}/{cluster}", 3),
                _ => throw InvalidDatabricksPath(),
            };

            var qualifiers = segments.Skip(endpointLength).ToArray();
            if (qualifiers.Length > 2 || qualifiers.Any(string.IsNullOrWhiteSpace))
                throw InvalidDatabricksPath();

            Specificator.Execute("HTTPPath", httpPath);
            if (qualifiers.Length > 0)
                Specificator.Execute("Catalog", qualifiers[0]);
            if (qualifiers.Length > 1)
                Specificator.Execute("Schema", qualifiers[1]);
        }

        private static InvalidConnectionUrlException InvalidDatabricksPath()
            => new("Expected /warehouse/<warehouse-id> or /cluster/<workspace-id>/<cluster-id>, optionally followed by /<catalog>[/<schema>].");
    }
}
