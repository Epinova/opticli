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
        SELECT TOP 1 cl.fkContentID, cl.fkLanguageBranchID
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
                .Where(c => !pagesOnly || model.Kind(c.TypeId) == ContentKind.Page)
                .Select(c => c.Segment)
                .ToList();

            var match = SegmentMatcher.Match(candidates, segment, pagesOnly ? parsed.Language?.Id : null);
            if (match is null)
            {
                if (walked.Count == 0 && pagesOnly && await SimpleAddressAsync(parsed.Segments, cancellationToken) is { } simple)
                {
                    return new ResolvedUrl(simple.ContentId, model.Language(simple.LanguageId), "simpleAddress", parsed.Site, parsed.Host, "simpleAddress");
                }
                throw NotFound(url, parsed, walked, current, segment, candidates);
            }

            current = match.ContentId;
            walked.Add(segment);
        }

        return new ResolvedUrl(current, pagesOnly ? parsed.Language : null, parsed.LanguageSource, parsed.Site, parsed.Host, "path");
    }

    private async Task<(int ContentId, int LanguageId)?> SimpleAddressAsync(IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        var address = "~/" + string.Join("/", segments);
        var rows = await db.QueryAsync(SimpleAddressSql, r => (r.GetInt32("fkContentID"), r.GetInt32("fkLanguageBranchID")), cancellationToken,
            new SqlParameter("@address", address), new SqlParameter("@addressWithSlash", address + "/"));
        return rows.Count > 0 ? rows[0] : null;
    }

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
