using System.Data.Common;
using System.Data.Odbc;
using DubUrl.Core.Testing.Authentication;
using DubUrl.Mapping;
using NUnit.Framework;

namespace DubUrl.Provider.Spark.QA.Connection;

public sealed class SparkKerberosTests : KerberosLiveContract
{
    [TestCase(false), TestCase(true), Category("SparkKerberos")]
    public Task ExistingTicketAuthenticatesConfiguredDatabaseAccount(bool asynchronous)
        => VerifyExistingTicketAsync("SPARK", Registry, asynchronous, "SELECT CURRENT_USER()");

    [TestCase(false), TestCase(true), Category("SparkKerberos")]
    public Task PluginOnlyAccountRejectsOtherOrMissingTicket(bool asynchronous)
        => VerifyRejectedTicketAsync("SPARK", Registry, asynchronous);

    private static SchemeRegistry Registry()
    {
        DbProviderFactories.RegisterFactory("System.Data.Odbc", OdbcFactory.Instance);
        return new ProviderCatalogBuilder().AddApplicationProviders().Build();
    }
}
