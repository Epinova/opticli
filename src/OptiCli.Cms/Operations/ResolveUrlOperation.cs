using System.Text.RegularExpressions;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Web;
using EPiServer.Web.Routing;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <param name="Content">The content the URL shows, in the language it shows it in.</param>
/// <param name="Url">Its canonical public URL, as templates link to it; null when it isn't routable.</param>
internal sealed record ResolvedUrl(ContentSummary Content, string? Url);

/// <summary>
/// Which content a URL shows: a public URL or path as visitors see it (<c>/en/about-us/</c>, with or without the host),
/// a permanent link (<c>/link/{guid}.aspx</c>), a link into the CMS edit UI (<c>...#context=epi.cms.contentdata:///123_456</c>),
/// or a plain ref. The CLI resolves URLs from the database; the MCP module has only the CMS's router.
/// </summary>
internal static partial class ResolveUrlOperation
{
    /// <exception cref="AgentException">
    /// <c>not_found</c> when nothing is at the URL, or the caller can't read what is, which is the same answer.
    /// </exception>
    public static ResolvedUrl Run(CmsCall call, string url)
    {
        var text = url?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw AgentException.Usage("url is required: a URL or path on this site, or a link into the CMS edit UI.");
        }
        var types = call.Service<IContentTypeRepository>();
        var urls = call.Service<IUrlResolver>();
        var content = Route(call, urls, text) is { } routed && call.CanRead(routed)
            ? routed
            : throw AgentException.NotFound($"No content is at '{Short(text)}'.",
                "Give the URL as visitors see it (the path alone works for this site's own hosts), or the content's id.");

        var language = (content as ILocale)?.Language;
        var publicUrl = urls.GetUrl(content.ContentLink.ToReferenceWithoutVersion(), language?.Name,
            new UrlResolverArguments { ContextMode = ContextMode.Default, ForceCanonical = true });
        return new ResolvedUrl(ContentSummaries.Describe(content, types), string.IsNullOrEmpty(publicUrl) ? null : publicUrl);
    }

    private static IContent? Route(CmsCall call, IUrlResolver urls, string text)
    {
        var locator = new ContentLocator(call);
        // An edit UI link names the version it opens; a permanent link the content's GUID; a ref itself.
        var reference = EditContext().Match(text) is { Success: true } edit ? edit.Groups["ref"].Value
            : PermanentLink().Match(text) is { Success: true } permanent ? Guid.ParseExact(permanent.Groups["guid"].Value, "N").ToString()
            : RefSyntax.TryParse(text, out _) ? text
            : null;
        if (reference is not null)
        {
            return Load(call, locator.Resolve(reference));
        }

        var arguments = new RouteArguments { ContextMode = ContextMode.Default, MatchWildcardHost = true };
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return urls.Route(new UrlBuilder(absolute), arguments)?.Content;
        }
        if (!text.StartsWith('/'))
        {
            text = "/" + text;
        }
        // A path: on this request's site first, then on each site's own URL, so a dedicated MCP host still finds them.
        return urls.Route(new UrlBuilder(text), arguments)?.Content
            ?? call.Service<ISiteDefinitionRepository>().List()
                .Where(site => site.SiteUrl is not null)
                .Select(site => urls.Route(new UrlBuilder(new Uri(site.SiteUrl, text)), arguments)?.Content)
                .FirstOrDefault(found => found is not null);
    }

    /// <summary>A version link loads that version; a content link the content in any language (its master).</summary>
    private static IContent? Load(CmsCall call, ContentReference link)
    {
        var repository = call.Service<IContentRepository>();
        if (link.WorkID > 0)
        {
            return repository.TryGet<IContent>(link, out var version) && version.ContentLink.ID == link.ID ? version : null;
        }
        return repository.TryGet<IContent>(link, new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }, out var content) ? content : null;
    }

    private static string Short(string text) => text.Length > 200 ? text[..200] + "…" : text;

    [GeneratedRegex(@"epi\.cms\.contentdata:///(?<ref>[0-9]+(?:_[0-9]+)?(?:__[A-Za-z0-9][A-Za-z0-9.-]*)?)")]
    private static partial Regex EditContext();

    [GeneratedRegex(@"/link/(?<guid>[0-9a-fA-F]{32})\.aspx", RegexOptions.IgnoreCase)]
    private static partial Regex PermanentLink();
}
