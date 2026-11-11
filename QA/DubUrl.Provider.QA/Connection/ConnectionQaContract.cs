using DubUrl.Discovery;
using DubUrl.Providers.QA.Contracts.BulkCopy;
using DubUrl.Providers.QA.Contracts.Schema;
using System.Data;
using NUnit.Framework;
using DubUrl.Mapping;
namespace DubUrl.Providers.QA.Contracts.Connection;
public abstract class ConnectionQaContract : PasswordAuthenticationQaContract
{
    protected abstract string CliConnectionUrl { get; }
    protected virtual ISchemeRegistry CliRegistry => DefaultRegistry.Get();
    protected override string AuthenticationConnectionUrl => CliConnectionUrl;
    protected override ISchemeRegistry AuthenticationRegistry => CliRegistry;
    protected abstract ConnectionUrl CreateLiveConnectionUrl();

    [Test, Category("Connection")]
    public void ConnectionCanCloseAndReopen()
    {
        using var connection = CreateLiveConnectionUrl().Connect();
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));

        connection.Open();
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));

        connection.Close();
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));

        connection.Open();
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Open));
    }

    protected virtual bool SupportsSchemaDiscovery => false;
    protected virtual bool SupportsBulkCopy => false;

    [Test, Category("SharedSchema")]
    public Task SchemaDiscoveryMatchesProviderSupport()
        => VerifySchemaDiscoveryAsync(CreateLiveConnectionUrl());

    protected virtual Task VerifySchemaDiscoveryAsync(ConnectionUrl url)
        => VerifySchemaDiscoveryAsync(url, null);

    protected Task VerifySchemaDiscoveryAsync(ConnectionUrl url, DiscoveryOptions? scope)
        => SchemaDiscoveryBaseline.VerifyAsync(url, SupportsSchemaDiscovery, scope);

    [Test, Category("SharedBulkCopy")]
    public void BulkCopyMatchesProviderSupport()
        => BulkCopyBaseline.Verify(CreateLiveConnectionUrl(), SupportsBulkCopy);

    protected virtual string CliTableName => "Customer";
    protected virtual string? CliEmptyTableName => null;
    protected virtual bool CliCanCreateTable => true;
}
