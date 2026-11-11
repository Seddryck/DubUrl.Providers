using System.Data.Common;
using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Connection;

public static class OdbcSmokeTest
{
    public static void Verify(ConnectionUrl url, DbProviderFactory factory)
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", factory);
        try
        {
            using var connection = url.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 60;
            Assert.That(command.ExecuteScalar(), Is.EqualTo(1));
        }
        catch (DbException exception)
        {
            // Native ODBC diagnostics can include pieces of the connection string.
            var state = exception.SqlState;
            if (state?.Length == 5 && state.All(char.IsAsciiLetterOrDigit))
                Assert.Fail($"The live ODBC connection or query failed (SQLSTATE {state}).");
            Assert.Fail("The live ODBC connection or query failed.");
        }
    }
}
