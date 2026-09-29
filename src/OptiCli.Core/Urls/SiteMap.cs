using System.Globalization;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;

namespace OptiCli.Core.Urls;

/// <summary>Where a URL path starts walking: a site's start page or one of the asset roots.</summary>
public enum UrlRoot
{
    StartPage,
    GlobalAssets,
    SiteAssets,
    ContentAssets,
}

/// <param name="Segments">Decoded path segments after the language and asset prefixes.</param>
/// <param name="LanguageSource">How the language was chosen: <c>path</c>, <c>host</c> or <c>site</c> (master language).</param>
public sealed record ParsedUrl(
    SiteInfo? Site,
    string? Host,
    UrlRoot Root,
    int RootId,
    LanguageBranch? Language,
    string? LanguageSource,
    IReadOnlyList<string> Segments);

/// <param name="Path">Host-relative path as the site serves it on <paramref name="Absolute"/>'s host.</param>
public sealed record ContentUrl(string Path, string? Absolute, string? Site);

/// <summary>
/// The routing rules opticli reproduces from the site definitions: which site and language a URL
/// selects, and what URL a content item has. Pure logic over <c>tblSiteDefinition</c> /
/// <c>tblHostDefinition</c> / <c>tblLanguageBranch</c>; the tree walk itself lives in <see cref="UrlResolver"/>.
/// </summary>
/// <remarks>
/// Pages: <c>/{language}/{segment}/.../</c>, where the language prefix is left out when a host of the site
/// is mapped to that language. Media: <c>/globalassets/...</c>, <c>/siteassets/...</c> or
/// <c>/contentassets/...</c>, no language prefix.
/// </remarks>
public sealed class SiteMap(IReadOnlyList<SiteInfo> sites, IReadOnlyList<LanguageBranch> languages, int? globalAssetsRoot, int? contentAssetsRoot)
{
    public const string Wildcard = "*";

    public IReadOnlyList<SiteInfo> All { get; } = sites;

    public int? GlobalAssetsRoot { get; } = globalAssetsRoot;

    public int? ContentAssetsRoot { get; } = contentAssetsRoot;

    public static int? StartPageId(SiteInfo site) => ParseId(site.StartPage);

    public static int? AssetsRootId(SiteInfo site) => ParseId(site.AssetsRoot);

    /// <summary><c>--site</c>: a site name, host name or id.</summary>
    /// <exception cref="NotFoundException">No such site.</exception>
    public SiteInfo RequireSite(string nameOrHost)
    {
        var input = nameOrHost.Trim();
        var site = All.FirstOrDefault(s => string.Equals(s.Name, input, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(s => s.Hosts.Any(h => string.Equals(h.Name, input, StringComparison.OrdinalIgnoreCase)))
            ?? All.FirstOrDefault(s => s.Id.ToString(CultureInfo.InvariantCulture) == input);
        return site ?? throw new NotFoundException(
            $"No site '{input}'.",
            Suggestions.DidYouMean(input, All.Select(s => s.Name).Concat(All.SelectMany(s => s.Hosts.Select(h => h.Name))))
                ?? $"Sites: {string.Join(", ", All.Select(s => s.Name))}.");
    }

    /// <summary>The site a bare path belongs to: the only site, or the one answering unknown hosts (<c>*</c>).</summary>
    /// <exception cref="UsageException">Several sites and none takes the wildcard host.</exception>
    public SiteInfo DefaultSite()
    {
        if (All.Count == 1)
        {
            return All[0];
        }
        return All.FirstOrDefault(s => s.Hosts.Any(h => h.Name == Wildcard))
            ?? throw new UsageException(
                $"{All.Count} sites are defined and none has the '*' host, so a path alone does not say which site it is on.",
                $"Pass --site <name|host> ({string.Join(", ", All.Select(s => s.Name))}) or a full URL.");
    }

    /// <summary>The language a URL without a language prefix gets on this site.</summary>
    public LanguageBranch? DefaultLanguage(SiteInfo site, HostInfo? host = null)
    {
        var code = host?.Language
            ?? site.Hosts.Where(h => h.Type == HostType.Primary).Select(h => h.Language).FirstOrDefault(l => l is not null)
            ?? site.Hosts.Where(h => h.Name == Wildcard).Select(h => h.Language).FirstOrDefault(l => l is not null)
            ?? site.MasterLanguage;
        return code is null ? null : ByCode(code);
    }

    /// <summary>
    /// The host a URL in <paramref name="language"/> is served from, and whether that host is mapped to
    /// the language (then the URL has no language prefix).
    /// </summary>
    public (HostInfo? Host, bool LanguageMapped) HostFor(SiteInfo site, LanguageBranch? language)
    {
        var usable = site.Hosts.Where(h => h.Name != Wildcard && h.Type is HostType.Primary or HostType.Undefined).ToList();
        var primary = usable.FirstOrDefault(h => h.Type == HostType.Primary) ?? usable.FirstOrDefault();
        if (language is null || language.IsInvariant)
        {
            return (primary, true);
        }

        var mapped = usable
            .Where(h => Matches(h.Language, language))
            .OrderBy(h => h.Type == HostType.Primary ? 0 : 1)
            .FirstOrDefault();
        if (mapped is not null)
        {
            return (mapped, true);
        }

        // Only the wildcard host carries the language: unknown hosts, and a primary host without a
        // language of its own, serve it without a prefix.
        var wildcardMapped = site.Hosts.Any(h => h.Name == Wildcard && Matches(h.Language, language));
        return (primary, wildcardMapped && primary?.Language is null);
    }

    /// <summary>
    /// Builds the URL of a page, media file or asset folder (<paramref name="kind"/>) from its path and a segment
    /// lookup. Returns null when the item is not under a start page (pages) or asset root (media and folders),
    /// or an ancestor has no URL segment.
    /// </summary>
    /// <remarks>
    /// Like the CMS, URLs end in a slash unless the last segment looks like a file name (<c>/robots.txt</c>,
    /// <c>/.well-known</c>, <c>/logo.png</c>); a media file named without an extension gets one too.
    /// </remarks>
    public ContentUrl? Compose(IReadOnlyList<int> pathIncludingSelf, Func<int, string?> segmentOf, LanguageBranch? language, ContentKind kind)
    {
        if (kind == ContentKind.Page)
        {
            foreach (var site in All)
            {
                var at = StartPageId(site) is { } start ? IndexOf(pathIncludingSelf, start) : -1;
                if (at < 0)
                {
                    continue;
                }
                if (Segments(pathIncludingSelf, at, segmentOf) is not { } segments)
                {
                    return null;
                }
                var (host, mapped) = HostFor(site, language);
                var prefix = mapped || language is null ? "" : "/" + language.UrlPrefix;
                var path = prefix + SlashPath(segments);
                return new ContentUrl(path, Absolute(site, host, path), site.Name);
            }
            return null;
        }

        var (rootId, keyword, owner) = AssetRoot(pathIncludingSelf);
        var root = rootId is { } assetRoot ? IndexOf(pathIncludingSelf, assetRoot) : -1;
        if (root < 0 || Segments(pathIncludingSelf, root, segmentOf) is not { } assetSegments || (assetSegments.Count == 0 && kind != ContentKind.Folder))
        {
            return null;
        }
        var assetPath = "/" + keyword + SlashPath(assetSegments);
        var assetSite = owner ?? (All.Count == 1 ? All[0] : null);
        return new ContentUrl(assetPath, assetSite is null ? null : Absolute(assetSite, HostFor(assetSite, null).Host, assetPath), assetSite?.Name);
    }

    /// <summary><c>/a/b/</c>, or <c>/a/b.txt</c> when the last segment has an extension; <c>/</c> for none.</summary>
    private static string SlashPath(IReadOnlyList<string> segments)
    {
        var path = string.Concat(segments.Select(s => "/" + s));
        return segments.Count > 0 && Path.HasExtension(segments[^1]) ? path : path + "/";
    }

    /// <summary>
    /// Splits a URL or path into site, root, language and remaining segments. The language prefix is
    /// recognised before page segments, as the CMS router does.
    /// </summary>
    /// <exception cref="UsageException">Not a URL, or a bare path when the site is ambiguous.</exception>
    /// <exception cref="NotFoundException">The host belongs to no site.</exception>
    public ParsedUrl Parse(string url, SiteInfo? siteOption)
    {
        var (authority, path) = SplitUrl(url);
        SiteInfo site;
        HostInfo? host = null;
        if (authority is not null && siteOption is null)
        {
            (site, host) = SiteForHost(authority);
        }
        else
        {
            site = siteOption ?? DefaultSite();
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();

        if (segments.Count > 0 && AssetKeyword(segments[0], site) is { } asset)
        {
            return new ParsedUrl(site, host?.Name, asset.Root, asset.Id, null, null, segments.Skip(1).ToList());
        }

        var start = StartPageId(site) ?? throw new NotFoundException($"Site '{site.Name}' has no start page.");
        if (segments.Count > 0 && languages.FirstOrDefault(l => l.Enabled && !l.IsInvariant
                && string.Equals(l.UrlPrefix, segments[0], StringComparison.OrdinalIgnoreCase)) is { } prefixed)
        {
            return new ParsedUrl(site, host?.Name, UrlRoot.StartPage, start, prefixed, "path", segments.Skip(1).ToList());
        }

        var hostLanguage = host?.Language is { } code ? ByCode(code) : null;
        return new ParsedUrl(
            site,
            host?.Name,
            UrlRoot.StartPage,
            start,
            hostLanguage ?? DefaultLanguage(site),
            hostLanguage is not null ? "host" : "site",
            segments);
    }

    private (SiteInfo Site, HostInfo Host) SiteForHost(string authority)
    {
        var bare = authority.Split(':')[0];
        foreach (var candidate in new[] { authority, bare })
        {
            foreach (var site in All)
            {
                if (site.Hosts.FirstOrDefault(h => string.Equals(h.Name, candidate, StringComparison.OrdinalIgnoreCase)) is { } host)
                {
                    return (site, host);
                }
            }
        }
        foreach (var site in All)
        {
            if (site.Hosts.FirstOrDefault(h => h.Name == Wildcard) is { } wildcard)
            {
                return (site, wildcard);
            }
        }
        throw new NotFoundException(
            $"No site answers host '{authority}'.",
            Suggestions.DidYouMean(authority, All.SelectMany(s => s.Hosts.Select(h => h.Name)))
                ?? "Run `opticli sites` to list hosts, or pass a path with --site.");
    }

    private (UrlRoot Root, int Id)? AssetKeyword(string segment, SiteInfo site) => segment.ToLowerInvariant() switch
    {
        "globalassets" when GlobalAssetsRoot is { } global => (UrlRoot.GlobalAssets, global),
        "contentassets" when ContentAssetsRoot is { } content => (UrlRoot.ContentAssets, content),
        "siteassets" when AssetsRootId(site) is { } assets => (UrlRoot.SiteAssets, assets),
        _ => null,
    };

    private (int? Root, string Keyword, SiteInfo? Owner) AssetRoot(IReadOnlyList<int> path)
    {
        if (All.FirstOrDefault(s => AssetsRootId(s) is { } assets && path.Contains(assets)) is { } site)
        {
            return (AssetsRootId(site), "siteassets", site);
        }
        if (GlobalAssetsRoot is { } global && path.Contains(global))
        {
            return (global, "globalassets", null);
        }
        if (ContentAssetsRoot is { } content && path.Contains(content))
        {
            return (content, "contentassets", null);
        }
        return (null, "", null);
    }

    private static List<string>? Segments(IReadOnlyList<int> path, int rootIndex, Func<int, string?> segmentOf)
    {
        var segments = new List<string>();
        for (var i = rootIndex + 1; i < path.Count; i++)
        {
            if (segmentOf(path[i]) is not { Length: > 0 } segment)
            {
                return null;
            }
            segments.Add(segment);
        }
        return segments;
    }

    private static string? Absolute(SiteInfo site, HostInfo? host, string path)
    {
        if (host is not null)
        {
            var scheme = host.Https switch
            {
                true => "https",
                false => "http",
                null => Uri.TryCreate(site.Url, UriKind.Absolute, out var siteUri) ? siteUri.Scheme : "https",
            };
            return $"{scheme}://{host.Name}{path}";
        }
        return Uri.TryCreate(site.Url, UriKind.Absolute, out var baseUri) ? new Uri(baseUri, path).ToString() : null;
    }

    private static (string? Authority, string Path) SplitUrl(string url)
    {
        var value = url.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}", uri.AbsolutePath);
        }
        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            value = value[1..];
        }
        if (!value.StartsWith('/'))
        {
            throw new UsageException($"'{url}' is not a URL or site-relative path.", "Pass a path like /en/about/ or a full URL like https://www.example.com/en/about/.");
        }
        var end = value.IndexOfAny(['?', '#']);
        return (null, end >= 0 ? value[..end] : value);
    }

    private LanguageBranch? ByCode(string code) =>
        languages.FirstOrDefault(l => !l.IsInvariant && string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(string? hostLanguage, LanguageBranch language) =>
        hostLanguage is not null && string.Equals(hostLanguage, language.Code, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<int> path, int id)
    {
        for (var i = 0; i < path.Count; i++)
        {
            if (path[i] == id)
            {
                return i;
            }
        }
        return -1;
    }

    private static int? ParseId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
