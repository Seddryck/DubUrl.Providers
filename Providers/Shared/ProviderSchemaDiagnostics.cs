using DubUrl.Discovery;

#if TEST_PROVIDER_SCHEMA_DIAGNOSTICS
namespace DubUrl.Providers.Testing.Discovery;
#else
namespace DubUrl.Providers.Discovery;
#endif

/// <summary>Records provider-specific rejections alongside diagnostics from the shared catalog mapper.</summary>
internal static class ProviderSchemaDiagnostics
{
    private static readonly AsyncLocal<List<DiscoveryDiagnostic>?> Current = new();

    internal static async Task<SchemaMetadata> ReadAsync(Func<Task<SchemaMetadata>> read)
    {
        var previous = Current.Value;
        var diagnostics = new List<DiscoveryDiagnostic>();
        Current.Value = diagnostics;
        try
        {
            var metadata = await read().ConfigureAwait(false);
            return diagnostics.Count == 0 ? metadata : metadata with
            {
                Diagnostics = [.. metadata.Diagnostics, .. diagnostics],
            };
        }
        finally
        {
            Current.Value = previous;
        }
    }

    internal static void Report(Action<SchemaFeature, string> diagnose, MetadataName table, SchemaFeature feature,
        string message, string? member = null, string? constraint = null, string? nativeDefinition = null)
    {
        var label = feature switch
        {
            SchemaFeature.Indexes => "Index",
            _ => "Column",
        };
        var context = member is null ? "" : $"{label} {QuoteIdentifier(member)}. ";
        if (constraint is not null)
            context += $"Constraint {QuoteIdentifier(constraint)}. ";
        var native = nativeDefinition is null ? "" : $" Native definition: {nativeDefinition}";
        if (Current.Value is { } diagnostics)
        {
            diagnostics.Add(new("SDI009", feature, MetadataCoverage.Unrepresentable, context + message + native, table)
            {
                Member = member ?? constraint,
                Constraint = constraint,
                NativeDefinition = nativeDefinition,
            });
        }
        else
            diagnose(feature, context + message + native);
    }

    private static string QuoteIdentifier(string name)
        => name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
