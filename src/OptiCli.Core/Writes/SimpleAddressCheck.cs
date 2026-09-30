using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Urls;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// Warns when a write gives a page a simple address another page of the same site already has in that language. The
/// CMS's own validator only checks it against URL segments, so such a clash is saved, and one of the two addresses
/// stops working.
/// </summary>
public static class SimpleAddressCheck
{
    private const string OthersSql = """
        SELECT cl.fkContentID, c.ContentPath FROM tblContentLanguage cl
        JOIN tblContent c ON c.pkID = cl.fkContentID
        WHERE cl.ExternalURL = @address AND cl.fkLanguageBranchID = @lang AND c.Deleted = 0 AND cl.fkContentID <> @self
        """;

    /// <summary>The address a change gives the page, as stored (<c>~/campaign</c>); null when it clears it or doesn't change it.</summary>
    public static string? Stored(IEnumerable<PropertyChange> changes) =>
        changes.FirstOrDefault(c => c.Property.Equals("SimpleAddress", StringComparison.OrdinalIgnoreCase)) is { After: { ValueKind: JsonValueKind.String } after }
            && ShortcutInfo.SimpleAddressPath(after.GetString()) is { } path
            ? "~" + path
            : null;

    /// <summary>The site whose start page is on the path (ancestors and the item itself), if any.</summary>
    public static SiteInfo? SiteOf(SiteMap sites, IEnumerable<int> path)
    {
        var ids = path.ToHashSet();
        return sites.All.FirstOrDefault(site => SiteMap.StartPageId(site) is { } start && ids.Contains(start));
    }

    /// <param name="path">Ids from the root to the page (or, for new content, to its parent).</param>
    /// <param name="self">The page itself; null for new content.</param>
    public static async Task<IReadOnlyList<string>> WarningsAsync(
        ContentSession session, WriteResult result, IReadOnlyList<int> path, int? self, CancellationToken cancellationToken)
    {
        if (Stored(result.Changes) is not { } address
            || (result.Content?.Language is { } code ? session.Model.LanguageByCode(code) : null) is not { } language)
        {
            return [];
        }
        var others = await session.Db.QueryAsync(OthersSql, r => (Id: r.GetInt32("fkContentID"), Path: r.GetString("ContentPath")), cancellationToken,
            new SqlParameter("@address", address), new SqlParameter("@lang", language.Id), new SqlParameter("@self", self ?? 0));
        var site = SiteOf(session.Model.Sites, path);
        var clashes = others.Where(o => SiteOf(session.Model.Sites, ContentPaths.Parse(o.Path).Append(o.Id)) == site).Select(o => o.Id).ToList();
        return clashes.Count == 0
            ? []
            : [$"The simple address {address[1..]} is already used by {string.Join(", ", clashes)} in the same site and language ({language.Code}); the CMS saves it anyway, but only one of them can be reached by it."];
    }
}
