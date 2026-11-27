using System.Globalization;
using DubUrl.Provider.Odbc.Rewriting;
using DubUrl.Adomd.Mapping;
using DubUrl.Mapping;
using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using DubUrl.Registering;
using NUnit.Framework;

namespace DubUrl.Provider.SsasTabular.QA.Connection;

[TestFixture, Category("SsasTabular"), NonParallelizable, Platform("Win")]
public sealed class SsasTabularTests : AdomdContract
{
    protected override SchemeRegistry CreateSchemeRegistry()
    {
        var assemblies = new[] { typeof(OdbcRewriter).Assembly, typeof(SsasTabularDatabase).Assembly };
        new ProviderFactoriesRegistrator(new BinFolderDiscoverer(assemblies)).Register();
        return new ProviderCatalogBuilder()
            .AddModule(new AttributeProviderModule([.. new AssemblyTypesProbe().Assemblies, .. assemblies])).Build();
    }

    protected override string ConnectionUrl => "ssastabular://localhost/tabular/AdventureWorks";
    protected override string InvalidConnectionUrl => "ssastabular://localhost/tabular/Missing";
    protected override string SelectFirstCustomerSql => string.Empty;
    protected override string SelectFirstCustomerRowSql => string.Empty;
    protected override string SelectCustomerByIdSql => string.Empty;
    protected override string SelectAllCustomersSql => string.Empty;
    protected override string SelectYoungestCustomersSql => string.Empty;
    protected override string SelectWhereCustomersTemplate => string.Empty;

    protected override bool SupportsCustomerQueries => false;
    protected override bool SupportsParameters => false;
    protected override bool SupportsRepositories => false;
    protected override bool SupportsMicroOrm => false;
    protected override bool SupportsDapper => false;

    [Test, Category("AdventureWorks")]
    public void QueryAdventureWorks()
    {
        using var connection = CreateLiveConnectionUrl().Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"EVALUATE ROW(""Internet Sales"", COUNTROWS('Internet Sales'))";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.IsDBNull(0), Is.False);
        Assert.That(Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture), Is.GreaterThan(0m));
    }

    [Test, Category("AdventureWorks")]
    public void QueryTableWithApostropheInTemplateIdentifier()
    {
        const string template = """
            DEFINE TABLE $table; format="identity"$ = ROW("Answer", 42)
            EVALUATE $table; format="identity"$
            """;
        var database = new DatabaseUrl(new ConnectionUrlFactory(CliRegistry), ConnectionUrl);
        var parameters = new Dictionary<string, object?> { ["table"] = "DubUrl's Template Test" };

        Assert.That(database.ReadScalarNonNull<decimal>(template, parameters), Is.EqualTo(42m));
    }
}
