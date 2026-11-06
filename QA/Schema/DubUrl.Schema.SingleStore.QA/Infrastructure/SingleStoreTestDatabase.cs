using SingleStoreConnector;
namespace DubUrl.Provider.SingleStore.QA;
internal static class SingleStoreTestDatabase
{
    public static SingleStoreConnection CreateConnection() => new(Environment.GetEnvironmentVariable("DUBURL_SINGLESTORE_QA_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Configure a disposable SingleStore database; see QA/README.md."));
}
