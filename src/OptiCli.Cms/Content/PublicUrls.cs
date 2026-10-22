using EPiServer.Core;
using EPiServer.Web;
using EPiServer.Web.Routing;

namespace OptiCli.Cms.Content;

/// <summary>Content's public URL, as templates link to it: never the edit or preview URL.</summary>
internal static class PublicUrls
{
    /// <param name="language">The language branch's code; the master language when null.</param>
    /// <param name="absolute">
    /// With the site's own host even for the site the request came in on (<see cref="CmsCall.AbsoluteUrls"/>), which the
    /// CMS otherwise leaves out.
    /// </param>
    /// <returns>Null for content that has no URL (a block, a folder).</returns>
    public static string? Of(IUrlResolver urls, ContentReference link, string? language, bool absolute)
    {
        var url = urls.GetUrl(
            link.ToReferenceWithoutVersion(),
            language,
            new UrlResolverArguments { ContextMode = ContextMode.Default, ForceCanonical = true, ForceAbsolute = absolute });
        return string.IsNullOrEmpty(url) ? null : url;
    }
}
