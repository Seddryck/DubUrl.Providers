using DubUrl.BulkCopy;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.BulkCopy;

internal static class BulkCopyBaseline
{
    internal static void Verify(ConnectionUrl url, bool expected)
    {
        using var connection = url.Open();
        var factory = new BulkCopyEngineFactory();
        if (expected)
        {
            using var engine = factory.Create(url);
            Assert.That(engine, Is.Not.Null);
        }
        else
        {
            Assert.That(() => factory.Create(url), Throws.TypeOf<NotSupportedException>());
        }
    }
}
