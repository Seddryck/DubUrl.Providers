using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DubUrl;
using DubUrl.Authentication;
using DubUrl.BulkCopy;
using DubUrl.Discovery;
using DubUrl.Mapping;

internal static class ConnectionUrlCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Generate(string root, string output)
    {
        var source = Path.Combine(root, "connection-urls");
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "catalog-source.json")))!.AsObject();
        var fragments = ReadFragments(root, source, catalog);
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "DubUrl.Provider.*.dll")
            .Concat(new[] { "DubUrl.OleDb.dll", "DubUrl.Adomd.dll" }.Select(name => Path.Combine(AppContext.BaseDirectory, name)))
            .Select(Assembly.LoadFrom).ToArray();
        var mappings = AttributeProviderModule.Discover(assemblies)
            .Concat(new NativeMapperIntrospector(assemblies).LocateAlternative()).ToArray();

        foreach (var database in catalog["databases"]!.AsArray())
        {
            var routes = new JsonArray();
            foreach (var reference in database!["routes"]!.AsArray())
            {
                var id = reference!.GetValue<string>();
                if (!fragments.Remove(id, out var route))
                    throw new InvalidOperationException($"Missing or repeated connection URL profile '{id}'.");
                AddRegistration(route, mappings);
                routes.Add(route);
            }
            database["routes"] = routes;
        }
        if (fragments.Count != 0)
            throw new InvalidOperationException("Connection URL profiles missing from the catalog index: "
                + string.Join(", ", fragments.Keys.Order(StringComparer.Ordinal)));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var json = catalog.ToJsonString(JsonOptions) + "\n";
        if (!File.Exists(output) || File.ReadAllText(output) != json)
            File.WriteAllText(output, json);
    }

    private static Dictionary<string, JsonObject> ReadFragments(string root, string source, JsonObject catalog)
    {
        var fragments = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(Path.Combine(root, "Providers"), "*.connection-urls.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(source, "*.connection-urls.json", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var fragment = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            MergeDefinitions(catalog, fragment, file);
            foreach (var item in fragment["routes"]!.AsArray())
            {
                var route = item!.DeepClone().AsObject();
                var id = route["id"]!.GetValue<string>();
                if (!fragments.TryAdd(id, route))
                    throw new InvalidOperationException($"Duplicate connection URL profile '{id}' in '{file}'.");
            }
        }
        return fragments;
    }

    private static void MergeDefinitions(JsonObject catalog, JsonObject fragment, string file)
    {
        foreach (var (section, definitions) in fragment)
        {
            if (section == "routes") continue;
            if (section is not ("locations" or "authentication" or "tlsPolicies"))
                throw new InvalidOperationException($"Unexpected connection URL fragment section '{section}' in '{file}'.");

            var entries = catalog[section]!.AsObject();
            foreach (var (id, definition) in definitions!.AsObject())
            {
                if (entries.ContainsKey(id))
                    throw new InvalidOperationException($"Duplicate {section} definition '{id}' in '{file}'.");
                entries.Add(id, definition!.DeepClone());
            }
        }
    }

    private static void AddRegistration(JsonObject route, MapperInfo[] mappings)
    {
        var id = route["id"]!.GetValue<string>();
        var scheme = route["scheme"]!.GetValue<string>();
        var invariant = route["providerInvariant"]!.GetValue<string>();
        var matches = mappings.Where(mapping => mapping.ProviderInvariantName == invariant
            && mapping.Aliases.Contains(scheme, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected one mapper for profile '{id}' using '{scheme}' and '{invariant}', found {matches.Length}.");

        var mapping = matches[0];
        if (route["schemeByExtension"] is JsonObject extensions)
            foreach (var (_, alias) in extensions)
                if (!mapping.Aliases.Contains(alias!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Unregistered file scheme '{alias}' in profile '{id}'.");

        if (route["schemeByLocation"] is JsonObject locations)
            foreach (var (_, modes) in locations)
                foreach (var (_, alias) in modes!.AsObject())
                    if (!mapping.Aliases.Contains(alias!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Unregistered endpoint scheme '{alias}' in profile '{id}'.");

        route["registeredSchemes"] = JsonSerializer.SerializeToNode(mapping.Aliases.Order(StringComparer.Ordinal));
        var mapper = mapping.MapperType;
        route["capabilities"] = JsonSerializer.SerializeToNode(new
        {
            tlsConfiguration = mapper.GetCustomAttribute<ConnectionConfigurationAttribute>(inherit: false)?.Tls,
            schemaDiscovery = mapper.IsDefined(typeof(SchemaDiscoveryAttribute), inherit: false),
            readOnly = mapper.IsDefined(typeof(ReadOnlyConnectionAttribute), inherit: false),
            fileSecret = mapper.IsDefined(typeof(FileSecretConnectionAttribute), inherit: false),
            bulkCopy = mapper.IsDefined(typeof(BulkCopyProviderAttribute), inherit: false),
            fileLoad = mapper.IsDefined(typeof(FileLoadAttribute), inherit: false),
        });
    }
}
