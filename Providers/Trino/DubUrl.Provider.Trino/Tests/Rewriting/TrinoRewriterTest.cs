using BAMCIS.PrestoClient;
using DubUrl.Authentication;
using DubUrl.Core.Testing;
using DubUrl.Core.Testing.Conversion;
using DubUrl.Core.Testing.Rewriting;
using DubUrl.Mapping;
using DubUrl.Parsing;
using DubUrl.Provider.Trino.Mapping;
using DubUrl.Provider.Trino.Rewriting;
using DubUrl.Querying.Dialects;
using DubUrl.Querying.Parametrizing;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Data.Common;
using NReco.PrestoAdo;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DubUrl.Provider.Trino.Tests.Rewriting;

public class TrinoRewriterTest
{
    private const string PROVIDER_NAME = "NReco.PrestoAdo";

    private static DbConnectionStringBuilder ConnectionStringBuilder
    {
        get => ConnectionStringBuilderHelper.Retrieve(PROVIDER_NAME, PrestoDbFactory.Instance);
    }

    [Test]
    [TestCase("host", "host")]
    [TestCase("", "localhost")]
    public void Map_UrlInfo_Host(string host, string expected)
    {
        var urlInfo = new ConnectionUri() { Host = host };
        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.ContainKey(TrinoRewriter.HOST_KEYWORD));
        Assert.That(result[TrinoRewriter.HOST_KEYWORD], Is.EqualTo(expected));
    }

    [Test]
    [TestCase(1234, "1234")]
    [TestCase(0, "8080")]
    public void Map_UrlInfo_Port(int port, string expected)
    {
        var urlInfo = new ConnectionUri() { Port = port };
        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.ContainKey(TrinoRewriter.PORT_KEYWORD));
        Assert.That(result[TrinoRewriter.PORT_KEYWORD], Is.EqualTo(expected));
    }

    [Test]
    [TestCase("catalog", "catalog")]
    public void Map_UrlInfo_Catalog(string segmentsList, string expected)
    {
        var urlInfo = new ConnectionUri() { Segments = segmentsList.Split('/') };
        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.ContainKey(TrinoRewriter.CATALOG_KEYWORD));
        Assert.That(result[TrinoRewriter.CATALOG_KEYWORD], Is.EqualTo(expected));
    }

    [Test]
    public void Map_UrlInfo_MissingCatalog()
    {
        var urlInfo = new ConnectionUri() { Segments = [] };
        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Not.ContainKey(TrinoRewriter.CATALOG_KEYWORD));
    }

    [Test]
    [TestCase("catalog/schema", "schema")]
    public void Map_UrlInfo_Schema(string segmentsList, string expected)
    {
        var urlInfo = new ConnectionUri() { Segments = segmentsList.Split('/') };
        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.ContainKey(TrinoRewriter.SCHEMA_KEYWORD));
        Assert.That(result[TrinoRewriter.SCHEMA_KEYWORD], Is.EqualTo(expected));
    }

    [Test]
    public void Map_UrlInfo_MissingSchema()
    {
        var urlInfo = new ConnectionUri() { Segments = ["catalog"] };
        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Not.ContainKey(TrinoRewriter.SCHEMA_KEYWORD));
    }

    [Test]
    public void Map_UrlInfoWithUsernamePassword_Authentication()
    {
        var urlInfo = new ConnectionUri() { Username = "user", Password = "pwd" };
        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.ContainKey(TrinoRewriter.USERNAME_KEYWORD));
            Assert.That(result[TrinoRewriter.USERNAME_KEYWORD], Is.EqualTo("user"));
            Assert.That(result, Does.ContainKey(TrinoRewriter.PASSWORD_KEYWORD));
            Assert.That(result[TrinoRewriter.PASSWORD_KEYWORD], Is.EqualTo("pwd"));
        });
    }

    [Test]
    public void Map_UrlInfo_HeaderOptions()
    {
        var urlInfo = new ConnectionUri() { };
        urlInfo = urlInfo.WithOption("TrinoHeaders", "2");

        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.ContainKey("TrinoHeaders"));
            Assert.That(result["TrinoHeaders"], Is.EqualTo("2"));
        });
    }

    [Test]
    public void Map_UrlInfo_MissingHeaderOptions()
    {
        var urlInfo = new ConnectionUri() { };

        var Rewriter = new TrinoRewriter(ConnectionStringBuilder);
        var result = Rewriter.Execute(urlInfo);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result, Does.ContainKey("TrinoHeaders"));
            Assert.That(result["TrinoHeaders"], Is.EqualTo("1"));
        });
    }

    [TestCase("", true, "1")]
    [TestCase("?TrinoHeaders=2", true, "2")]
    [TestCase("?usessl=false", false, "1")]
    [TestCase("?UseSsl=true&TrinoHeaders=2", true, "2")]
    public void Parse_NativeTrinoUrl_PreservesCredentialsAndResolvesOptions(
        string options, bool useSsl, string headers)
    {
        var target = new ConnectionUrl($"trino://alice:test-password@trino.example.com:8443/pg/public{options}", CreateRegistry());
        var settings = new PrestoConnectionStringBuilder { ConnectionString = target.Parse() };

        Assert.Multiple(() =>
        {
            Assert.That(settings.UseSsl, Is.EqualTo(useSsl));
            Assert.That(settings["TrinoHeaders"], Is.EqualTo(headers));
            Assert.That(settings.Host, Is.EqualTo("trino.example.com"));
            Assert.That(settings.Port, Is.EqualTo(8443));
            Assert.That(settings.User, Is.EqualTo("alice"));
            Assert.That(settings.Password, Is.EqualTo("test-password"));
        });
    }

    [TestCase("", true)]
    [TestCase("?UseSsl=false", false)]
    public void Connect_NativeTrinoWithRuntimeCredentials_UsesResolvedTls(string options, bool useSsl)
    {
        var target = new ConnectionUrl($"trino://trino.example.com/pg/public{options}", CreateRegistry())
            .WithAuthentication(ConnectionAuthentication.UsernamePassword("alice", "test-password"));

        using var connection = (PrestoConnection)target.Connect();
        // NReco 1.1.0 does not retain the assigned ConnectionString.
        var settings = (PrestoClientSessionConfig)typeof(PrestoConnection)
            .GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;

        Assert.Multiple(() =>
        {
            Assert.That(settings.UseSsl, Is.EqualTo(useSsl));
            Assert.That(settings.Port, Is.EqualTo(8080));
            Assert.That(settings.User, Is.EqualTo("alice"));
            Assert.That(settings.Password, Is.EqualTo("test-password"));
        });
    }

    private static SchemeRegistry CreateRegistry()
        => ConversionTestSupport.Registry("trino", PrestoDbFactory.Instance,
            builder => new TrinoMapper(builder, ConversionTestSupport.Dialect, ConversionTestSupport.Parametrizer));
}
