using DubUrl.Provider.Access.Sql;
using DubUrl.Provider.MySql.Sql;
using DubUrl.Provider.Oracle.Sql;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Dialects.Formatters;
using DubUrl.Querying.Parametrizing;
using DubUrl.Querying.Templating;
using NUnit.Framework;

namespace DubUrl.Providers.Testing;

public class ProviderIdentifierRenderingTests
{
    private static readonly IDialectRegistry Dialects = CreateDialects();

    [TestCase("oracle", "private\"name", "Oracle identifiers cannot contain double quotes or NUL characters.")]
    [TestCase("oracle", "private\0name", "Oracle identifiers cannot contain double quotes or NUL characters.")]
    [TestCase("access", "private[name", "Access identifiers cannot contain square brackets.")]
    [TestCase("access", "private]name", "Access identifiers cannot contain square brackets.")]
    public void RenderCommand_ForbiddenIdentifier_ThrowsDialectError(string alias, string name, string message)
    {
        var template = new InlineTemplateCommandFactory(Dialects.Get(alias), new NamedParametrizer())
            .Create("SELECT 1 FROM $name; format=\"identity\"$");
        var values = new Dictionary<string, object?> { ["name"] = name };

        Assert.That(() => template.RenderCommand(values), Throws.TypeOf<InvalidIdentifierException>().With.Message.EqualTo(message));
    }

    [TestCase("oracle", "two words", "\"two words\"")]
    [TestCase("access", "two words", "[two words]")]
    [TestCase("mysql", "a`b", "`a``b`")]
    [TestCase("singlestore", "a`b", "`a``b`")]
    public void Render_Identifier_UsesDialectQuoting(string alias, string name, string expected)
        => Assert.That(Dialects.Get(alias).Renderer.Render(name, "identity"), Is.EqualTo(expected));

    private static IDialectRegistry CreateDialects()
    {
        var builder = new DialectRegistryBuilder();
        builder.AddDialect<OracleDialect>(["oracle"]);
        builder.AddDialect<MsAccessDialect>(["access"]);
        builder.AddDialect<MySqlDialect>(["mysql"]);
        builder.AddDialect<SingleStoreDialect>(["singlestore"]);
        return builder.Build();
    }
}
