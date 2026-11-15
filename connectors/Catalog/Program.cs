using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DubUrl.Core.ProviderPacks;
using DubUrl.Mapping;

if (args is ["connection-urls", var root, var output])
{
    ConnectionUrlCatalog.Generate(root, output);
    return;
}

if (args.Length != 4) throw new ArgumentException("Expected matrix path, output path, connector version, and minimum DubUrl version.");
if (!Regex.IsMatch(args[2], @"^\d+\.\d+\.\d+(?:[-+].+)?$", RegexOptions.None, TimeSpan.FromSeconds(1)))
    throw new ArgumentException("Invalid connector version.");
var coreVersion = Version.Parse(args[3]);
if (coreVersion.Build < 0 || coreVersion.Revision >= 0)
    throw new ArgumentException("Expected a stable three-part DubUrl version.");
var apiRange = $"[{coreVersion},{coreVersion.Major + 1}.0.0)";
using var matrix = JsonDocument.Parse(File.ReadAllText(args[0]));
if (matrix.RootElement.GetProperty("schemaVersion").GetInt32() != 2) throw new InvalidOperationException("Expected matrix schema version 2.");
var rows = matrix.RootElement.GetProperty("connectors").EnumerateArray()
    .Where(row => row.GetProperty("status").GetString() == "ready").OrderBy(row => row.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
foreach (var key in new[] { "id" })
    if (rows.Select(row => row.GetProperty(key).GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Length)
        throw new InvalidOperationException($"Connector {key} values must be unique.");
var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "DubUrl.Provider.*.dll")
    .Append(Path.Combine(AppContext.BaseDirectory, "DubUrl.OleDb.dll")).Select(Assembly.LoadFrom).ToArray();
var mappings = AttributeProviderModule.Discover(assemblies);
var selections = rows.Select(row =>
{
    string Value(string name) => row.GetProperty(name).GetString()!;
    var mapping = AttributeProviderModule.SelectMapping(mappings, Value("databaseScheme"), Value("technology"));
    var implementations = new[] { "DubUrl.Provider." + Value("family") + ".dll" }
        .Concat(Value("technology") == "System.Data.Odbc" ? new[] { "DubUrl.Provider.Odbc.dll" }
            : Value("technology") == "System.Data.OleDb" ? new[] { "DubUrl.OleDb.dll" } : Array.Empty<string>()).Distinct().ToArray();
    return new { Row = row, Mapping = mapping, Implementations = implementations,
        Key = string.Join("|", implementations.Order(StringComparer.Ordinal)) + "|" + Value("technology")
            + "|" + string.Join("|", row.GetProperty("runtimeIdentifiers").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal)) };
}).ToArray();
var entries = selections.GroupBy(selection => selection.Key).Select(group =>
{
    // Prefer the family installation name, retaining every other database ID as an alias.
    var canonical = group.OrderBy(selection => selection.Row.GetProperty("id").GetString()!
        .StartsWith(selection.Row.GetProperty("family").GetString()!.ToLowerInvariant() + "-", StringComparison.Ordinal) ? 0 : 1)
        .ThenBy(selection => selection.Row.GetProperty("id").GetString(), StringComparer.Ordinal).First();
    var row = canonical.Row;
    var driver = row.GetProperty("technology").GetString()!;
    var supported = mappings.Where(mapping => mapping.ProviderInvariantName == driver
        && canonical.Implementations.Contains(mapping.MapperType.Assembly.GetName().Name + ".dll")
        && canonical.Implementations.Contains(mapping.Database!.Type.Assembly.GetName().Name + ".dll")).ToArray();
    var databases = supported.Select(mapping => new ConnectorDatabase(
        mapping.Database!.Name,
        mapping.Aliases)).OrderBy(database => database.Name, StringComparer.Ordinal).ToArray();
    var id = row.GetProperty("id").GetString()!;
    return new ConnectorDefinition
    {
        Provider = id, Databases = databases, Version = args[2],
        DubUrlApiRange = apiRange,
        Assembly = canonical.Mapping.Database!.Type.Assembly.GetName().Name + ".dll",
        ImplementationAssemblies = canonical.Implementations,
        Driver = driver,
        DriverAssembly = matrix.RootElement.GetProperty("technologies").EnumerateArray()
            .Single(technology => technology.GetProperty("name").GetString() == driver)
            .TryGetProperty("assembly", out var driverAssembly) ? driverAssembly.GetString() + ".dll" : null,
        Aliases = group.SelectMany(selection => selection.Row.GetProperty("defaultAliases").EnumerateArray().Select(item => item.GetString()!)
            .Prepend(selection.Row.GetProperty("id").GetString()!)).Where(alias => alias != id).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
        TargetFrameworks = ["net8.0", "net9.0", "net10.0"],
        RuntimeIdentifiers = row.GetProperty("runtimeIdentifiers").EnumerateArray().Select(item => item.GetString()!).ToArray()
    };
}).OrderBy(entry => entry.Provider, StringComparer.Ordinal).ToArray();
var json = JsonSerializer.Serialize(new { schemaVersion = ProviderPackCatalogDocument.CurrentSchemaVersion, entries }, new JsonSerializerOptions
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + "\n";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
if (!File.Exists(args[1]) || File.ReadAllText(args[1]) != json) File.WriteAllText(args[1], json);
