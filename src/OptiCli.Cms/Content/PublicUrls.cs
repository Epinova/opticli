using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using EPiServer.Web.Routing;

namespace OptiCli.Cms.Content;

/// <summary>Content's public URL, as templates link to it: never the edit or preview URL.</summary>
internal static class PublicUrls
{
    /// <param name="language">The language branch's code; the master language when null.</param>
    /// <param name="absolute">
    /// With the site's host (<see cref="CmsCall.AbsoluteUrls"/>), which the CMS otherwise leaves out for the site the request
    /// came in on. Without it, the URL is host-relative whatever the CMS gives (<see cref="HostRelative"/>), except a page's
    /// whose shortcut is an external link: that link is its URL, on another host.
    /// </param>
    /// <returns>Null for content that has no URL (a block, a folder).</returns>
    public static string? Of(IUrlResolver urls, ContentReference link, string? language, bool absolute)
    {
        var url = urls.GetUrl(
            link.ToReferenceWithoutVersion(),
            language,
            new UrlResolverArguments { ContextMode = ContextMode.Default, ForceCanonical = true, ForceAbsolute = absolute });
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        if (absolute || HostRelative(url) is var relative && relative == url)
        {
            return url;
        }
        return IsExternalShortcut(link, language) ? url : relative;
    }

    /// <summary>Whether the page's shortcut, in <paramref name="language"/>, is an external link.</summary>
    private static bool IsExternalShortcut(ContentReference link, string? language)
    {
        IContentLoader? loader = null;
        try
        {
            ServiceLocator.Current.TryGetExistingInstance(out loader);
        }
        catch (InvalidOperationException)
        {
            // No service locator (a unit test).
        }
        var reference = link.ToReferenceWithoutVersion();
        PageData? page = null;
        if (loader is not null)
        {
            page = language is null
                ? loader.TryGet<PageData>(reference, out var master) ? master : null
                : loader.TryGet<PageData>(reference, CultureInfo.GetCultureInfo(language), out var branch) ? branch : null;
        }
        return page?.LinkType == PageShortcutType.External;
    }

    /// <summary>
    /// <paramref name="url"/> without its scheme and host: the path (with query and fragment) as the CLI's reads from the
    /// database give it. The canonical URL carries the site's primary host whenever the request isn't on it, as when
    /// <c>opticli serve</c> runs the site on another port than its hosts name, or a site's primary host is a production
    /// name; that host doesn't answer locally, while the path does on the served address.
    /// </summary>
    internal static string HostRelative(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("//", StringComparison.Ordinal))
        {
            return url;
        }
        var authority = url.IndexOf("//", StringComparison.Ordinal) + 2;
        var path = url.IndexOfAny(['/', '?', '#'], authority);
        return path < 0 ? "/" : url[path] == '/' ? url[path..] : "/" + url[path..];
    }
}
