using NUnit.Framework;

// Global setup applies to every fixture in each provider QA assembly,
// including the retained provider-specific regression fixtures.
[SetUpFixture]
public sealed class DatabaseFrameworkGuard
{
    [OneTimeSetUp]
    public void RequireDotNet10()
        => Assert.That(Environment.Version.Major, Is.EqualTo(10),
            "Real database QA executes on .NET 10 only.");
}
