namespace DubUrl.Provider.Sqlite.QA;
internal static class SqliteTestDatabase
{
    public static string ProviderConnectionString => "Data Source=:memory:";
    public static void EnsureInitialized() => SQLitePCL.Batteries_V2.Init();
}
