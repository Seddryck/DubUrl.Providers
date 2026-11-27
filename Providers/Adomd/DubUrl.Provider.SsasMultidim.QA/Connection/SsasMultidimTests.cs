using System.Globalization;
using DubUrl.Provider.Odbc.Rewriting;
using DubUrl.Adomd.Mapping;
using DubUrl.Mapping;
using DubUrl.ProviderTesting;
using DubUrl.Providers.QA.Contracts.Connection;
using DubUrl.Providers.QA.Contracts.Query;
using DubUrl.Registering;
using NUnit.Framework;

namespace DubUrl.Provider.SsasMultidim.QA.Connection;

[TestFixture, Category("SsasMultidim"), NonParallelizable, Platform("Win")]
public sealed class SsasMultidimTests : AdomdContract
{
    protected override SchemeRegistry CreateSchemeRegistry()
    {
        var assemblies = new[] { typeof(OdbcRewriter).Assembly, typeof(SsasMultidimDatabase).Assembly };
        new ProviderFactoriesRegistrator(new BinFolderDiscoverer(assemblies)).Register();
        return new ProviderCatalogBuilder()
            .AddModule(new AttributeProviderModule([.. new AssemblyTypesProbe().Assemblies, .. assemblies])).Build();
    }

    protected override string ConnectionUrl => "ssasmultidim://localhost/multidim/AdventureWorks";
    protected override string InvalidConnectionUrl => "ssasmultidim://localhost/multidim/Missing";
    protected override string SelectFirstCustomerSql => string.Empty;
    protected override string SelectFirstCustomerRowSql => string.Empty;
    protected override string SelectCustomerByIdSql => string.Empty;
    protected override string SelectAllCustomersSql => string.Empty;
    protected override string SelectYoungestCustomersSql => string.Empty;
    protected override string SelectWhereCustomersTemplate => string.Empty;

    protected override bool SupportsPrimitives => false;
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
        command.CommandText = @"SELECT {[Measures].[Internet Sales Amount]} ON COLUMNS FROM [Adventure Works]";
        using var reader = command.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.IsDBNull(0), Is.False);
        Assert.That(Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture), Is.GreaterThan(0m));
    }

    [Test, Category("AdventureWorks")]
    public void QueryMemberWithClosingBracketInTemplateIdentifier()
    {
        const string template = """
            WITH MEMBER [Measures].$member; format="identity"$ AS 42
            SELECT {[Measures].$member; format="identity"$} ON COLUMNS
            FROM [Adventure Works]
            """;
        var database = new DatabaseUrl(new ConnectionUrlFactory(CliRegistry), ConnectionUrl);
        var parameters = new Dictionary<string, object?> { ["member"] = "DubUrl]Template Test" };

        Assert.That(database.ReadScalarNonNull<decimal>(template, parameters), Is.EqualTo(42m));
    }
}
