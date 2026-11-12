using DubUrl.Discovery;
using DubUrl.Schema;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Schema;

internal static class SchemaDiscoveryBaseline
{
    internal static async Task VerifyAsync(ConnectionUrl url, bool expected, DiscoveryOptions? scope = null)
    {
        if (!expected)
        {
            using var connection = url.Open();
            var unsupported = await new SchemaInspector().InspectAsync(url);
            Assert.That(unsupported.IsSuccess, Is.False);
            Assert.That(unsupported.Diagnostics.Select(x => x.Code), Is.EqualTo(new[] { "SDI001" }));
            return;
        }

        var result = await new SchemaInspector().InspectAsync(url, (scope ?? new DiscoveryOptions()) with
        {
            Features = [SchemaFeature.Tables]
        });
        Assert.That(result.IsSuccess, Is.True, string.Join(", ", result.Diagnostics.Select(x => x.Code)));
        Assert.That(result.Schema!.Tables.Values.Select(x => x.Name),
            Has.Some.EqualTo("Customer").IgnoreCase);
    }
}
