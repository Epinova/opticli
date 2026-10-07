using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;

namespace OptiCli.Core.Urls;

/// <param name="Language">The language the URL selects; null for media.</param>
/// <param name="MatchedBy"><c>path</c> (segments walked from the root) or <c>simpleAddress</c>.</param>
public sealed record ResolvedUrl(int ContentId, LanguageBranch? Language, string? LanguageSource, SiteInfo? Site, string? Host, string MatchedBy);

/// <summary>
/// URL → content: picks site and language with <see cref="SiteMap.Parse"/>, then walks URL segments down
/// from the start page (or asset root) one level at a time, the way the CMS's partial router does.
/// </summary>
public sealed class UrlResolver(CmsDatabase db, CmsModel model)
{
    private const string ChildrenSql = """
        SELECT c.pkID, c.fkContentTypeID, cl.fkLanguageBranchID, cl.URLSegment, cl.Status
        FROM tblContent c
        JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID
        WHERE c.fkParentID = @parent AND c.Deleted = 0 AND cl.URLSegment IS NOT NULL AND cl.URLSegment <> ''
        """;

    private const string SimpleAddressSql = """
        SELECT cl.fkContentID, cl.fkLanguageBranchID, ISNULL(c.ContentPath, '') AS ContentPath
        FROM tblContentLanguage cl
        JOIN tblContent c ON c.pkID = cl.fkContentID AND c.Deleted = 0
        WHERE cl.ExternalURL IN (@address, @addressWithSlash)
        ORDER BY cl.fkContentID
        """;

    /// <exception cref="NotFoundException">No content has this URL; the hint says how far the walk got.</exception>
    public async Task<ResolvedUrl> ResolveAsync(string url, SiteInfo? site, CancellationToken cancellationToken)
    {
        var parsed = model.Sites.Parse(url, site);
        var pagesOnly = parsed.Root == UrlRoot.StartPage;
        var current = parsed.RootId;
        var walked = new List<string>();

        foreach (var segment in parsed.Segments)
        {
            var children = await db.QueryAsync(ChildrenSql, r => (
                    Segment: new ChildSegment(r.GetInt32("pkID"), r.GetInt32("fkLanguageBranchID"), r.GetString("URLSegment"),
                        VersionStatuses.From(r.GetInt32OrNull("Status")) == VersionStatus.Published),
                    TypeId: r.GetInt32("fkContentTypeID")),
                cancellationToken, new SqlParameter("@parent", current));
            var candidates = children
                .Where(c => !pagesOnly || model.Kind(c.TypeId).IsPage())
                .Select(c => c.Segment)
                .ToList();

            var match = SegmentMatcher.Match(candidates, segment, pagesOnly ? parsed.Language?.Id : null);
            if (match is null)
            {
                // The CMS tries simple addresses after the page tree, for the whole path after the language prefix.
                if (pagesOnly && await SimpleAddressAsync(parsed, cancellationToken) is { } simple)
                {
                    return parsed.LanguageSource == "path"
                        ? new ResolvedUrl(simple.ContentId, parsed.Language, "path", parsed.Site, parsed.Host, "simpleAddress")
                        : new ResolvedUrl(simple.ContentId, model.Language(simple.LanguageId), "simpleAddress", parsed.Site, parsed.Host, "simpleAddress");
                }
                throw NotFound(url, parsed, walked, current, segment, candidates);
            }

            current = match.ContentId;
            walked.Add(segment);
        }

        return new ResolvedUrl(current, pagesOnly ? parsed.Language : null, parsed.LanguageSource, parsed.Site, parsed.Host, "path");
    }

    private async Task<(int ContentId, int LanguageId)?> SimpleAddressAsync(ParsedUrl parsed, CancellationToken cancellationToken)
    {
        var address = "~/" + string.Join("/", parsed.Segments);
        var rows = await db.QueryAsync(SimpleAddressSql, r => new SimpleAddressRow(
                r.GetInt32("fkContentID"), r.GetInt32("fkLanguageBranchID"), ContentPaths.Parse(r.GetString("ContentPath")).Append(r.GetInt32("fkContentID")).ToList()),
            cancellationToken, new SqlParameter("@address", address), new SqlParameter("@addressWithSlash", address + "/"));
        return BestSimpleAddress(model.Sites, rows, parsed.Site, parsed.Language) is { } best ? (best.ContentId, best.LanguageId) : null;
    }

    /// <param name="Path">Ids from the root to the page itself.</param>
    internal sealed record SimpleAddressRow(int ContentId, int LanguageId, IReadOnlyList<int> Path);

    /// <summary>
    /// The page a simple address leads to on <paramref name="site"/>, as the CMS picks it: only pages of that site (or of
    /// no site) count, and a branch in the requested language goes before others; then the lowest id.
    /// </summary>
    internal static SimpleAddressRow? BestSimpleAddress(SiteMap sites, IEnumerable<SimpleAddressRow> rows, SiteInfo? site, LanguageBranch? language) =>
        rows
            .Where(row => sites.SiteOf(row.Path) is not { } owner || site is null || owner.Id == site.Id)
            .OrderBy(row => row.LanguageId == language?.Id ? 0 : 1)
            .ThenBy(row => row.ContentId)
            .FirstOrDefault();

    private NotFoundException NotFound(string url, ParsedUrl parsed, List<string> walked, int current, string segment, IReadOnlyList<ChildSegment> candidates)
    {
        var inLanguage = candidates.Where(c => parsed.Language is null || c.LanguageId == parsed.Language.Id).Select(c => c.Segment);
        var language = parsed.Language is null ? "" : $" in language '{parsed.Language.Code}' ({parsed.LanguageSource})";
        var root = parsed.Root switch
        {
            UrlRoot.StartPage => "the start page",
            UrlRoot.GlobalAssets => "/globalassets/",
            UrlRoot.SiteAssets => "/siteassets/",
            _ => "/contentassets/",
        };
        var reached = walked.Count == 0 ? root : $"{root} → /{string.Join("/", walked)}/";
        return new NotFoundException(
            $"No content at '{url}'{language}: under {reached} (content {current}) there is no child '{segment}'.",
            Suggestions.DidYouMean(segment, inLanguage)
                ?? $"Run `opticli children {current}` to see what is there{(parsed.Site is null ? "" : $" (site '{parsed.Site.Name}')")}.");
    }
}
