using Oracle.ManagedDataAccess.Client;
using DubUrl.Providers.QA.Contracts.Schema;
namespace DubUrl.Provider.Oracle.QA;
internal static class OracleTestDatabase
{
    public static OracleConnection Connect() => new(
        Environment.GetEnvironmentVariable("DUBURL_ORACLE_QA_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Configure a disposable Oracle database; see QA/Schema/README.md."));

    public static OracleConnection Open()
    {
        var connection = Connect();
        connection.Open();
        return connection;
    }

    public static OracleTestSchema CreateSchema()
    {
        using var admin = Open();
        var name = "DUBURL_QA_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var password = "Qa_" + Guid.NewGuid().ToString("N");
        var schema = new OracleTestSchema(name, new OracleConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = name, Password = password, Pooling = false
        }.ConnectionString);
        var tablespace = QaSql.Text(QaSql.Rows(admin, """
            SELECT PROPERTY_VALUE FROM DATABASE_PROPERTIES WHERE PROPERTY_NAME = 'DEFAULT_PERMANENT_TABLESPACE'
            """).Single()[0]).Replace("\"", "\"\"", StringComparison.Ordinal);
        QaSql.Execute(admin, $"CREATE USER {name} IDENTIFIED BY \"{password}\" QUOTA 16M ON \"{tablespace}\"");
        try
        {
            QaSql.Execute(admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO {name}");
            return schema;
        }
        catch
        {
            schema.Dispose();
            throw;
        }
    }
}

internal sealed class OracleTestSchema(string name, string connectionString) : IDisposable
{
    public string Name { get; } = name;

    public OracleConnection Connect() => new(connectionString);

    public void Dispose()
    {
        using var admin = OracleTestDatabase.Open();
        QaSql.Execute(admin, $"DROP USER {Name} CASCADE");
    }
}
