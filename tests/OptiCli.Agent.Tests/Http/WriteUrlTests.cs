using EPiServer;
using EPiServer.Core;
using EPiServer.Web.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Http;

/// <summary>The agent's write results carry the content's public URL, so the CLI needs no second request for it.</summary>
public class WriteUrlTests
{
    private static readonly ContentSummary Page = new() { Ref = "12_34", Id = 12, Version = 34, Guid = Guid.NewGuid(), Name = "News", Language = "sv", Status = "checkedOut" };

    [Fact]
    public void A_write_gets_the_contents_url_in_its_language_without_the_version()
    {
        var urls = new StubUrlResolver(arguments => arguments.ForceAbsolute ? "http://alloy-sv.localhost:5199/sv/news/" : "/sv/news/");

        var result = AgentMiddleware.WithUrl(Request(urls), new WriteResult { Content = Page, Saved = true });

        Assert.Equal("/sv/news/", result.Url);
        // With the site's host too: which site of several the content is on.
        Assert.Equal("http://alloy-sv.localhost:5199/sv/news/", result.CanonicalUrl);
        Assert.Equal(2, urls.Calls.Count);
        foreach (var (link, language, arguments) in urls.Calls)
        {
            Assert.Equal(new ContentReference(12), link);
            Assert.Equal("sv", language);
            Assert.True(arguments.ForceCanonical);
        }
        Assert.Equal([false, true], urls.Calls.Select(c => c.Arguments.ForceAbsolute));
    }

    [Fact]
    public void Content_without_a_url_and_a_dry_run_create_get_none()
    {
        var urls = new StubUrlResolver(_ => "");

        Assert.Null(AgentMiddleware.WithUrl(Request(urls), new WriteResult { Content = Page }).Url);
        Assert.Null(AgentMiddleware.WithUrl(Request(urls), new WriteResult { DryRun = true }).Url);
        Assert.Single(urls.Calls);
    }

    [Fact]
    public void A_url_the_site_fails_to_give_leaves_the_write_as_it_is()
    {
        var written = new WriteResult { Content = Page, Saved = true };

        var result = AgentMiddleware.WithUrl(Request(new StubUrlResolver(_ => throw new InvalidOperationException("router"))), written);

        Assert.Same(written, result);
    }

    private static AgentRequest Request(IUrlResolver urls) =>
        new(new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(urls).BuildServiceProvider() }, null);

    private sealed class StubUrlResolver(Func<UrlResolverArguments, string> url) : IUrlResolver
    {
        public List<(ContentReference Link, string Language, UrlResolverArguments Arguments)> Calls { get; } = [];

        public string GetUrl(ContentReference contentLink, string language, UrlResolverArguments urlResolverArguments)
        {
            Calls.Add((contentLink, language, urlResolverArguments));
            return url(urlResolverArguments);
        }

        public string GetUrl(UrlBuilder urlBuilderWithInternalUrl, UrlResolverArguments arguments) => throw new NotSupportedException();

        public ContentRouteData Route(UrlBuilder urlBuilder, RouteArguments routeArguments) => throw new NotSupportedException();

        public bool TryToPermanent(string url, out string permanentUrl) => throw new NotSupportedException();
    }
}
