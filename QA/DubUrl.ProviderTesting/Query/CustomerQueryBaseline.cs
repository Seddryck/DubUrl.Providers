using NUnit.Framework;

namespace DubUrl.Providers.QA.Contracts.Query;

internal static class CustomerQueryBaseline
{
    internal static void Verify(ConnectionUrl url, string sql)
    {
        using var connection = url.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.That(command.ExecuteScalar(), Is.EqualTo("Nikola Tesla"));
    }
}
