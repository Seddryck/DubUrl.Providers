using Oracle.ManagedDataAccess.Client;
namespace DubUrl.Provider.Oracle.QA;
internal static class OracleTestDatabase
{
    public static OracleConnection Open()
    {
        var value = Environment.GetEnvironmentVariable("DUBURL_ORACLE_QA_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Configure a disposable Oracle database; see QA/README.md.");
        var connection = new OracleConnection(value);
        connection.Open();
        return connection;
    }
}
