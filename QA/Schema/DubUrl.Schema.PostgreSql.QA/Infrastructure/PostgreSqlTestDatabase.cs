namespace DubUrl.Provider.PostgreSql.QA;
internal static class PostgreSqlTestDatabase
{
    public static string ProviderConnectionString => Environment.GetEnvironmentVariable("DUBURL_POSTGRESQL_QA_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Configure a disposable PostgreSQL database; see QA/README.md.");
}
