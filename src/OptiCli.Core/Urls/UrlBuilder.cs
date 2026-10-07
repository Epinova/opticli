using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Urls;

/// <summary>
/// Content → URL. Loads the URL segments of every ancestor it has not seen yet in one query per batch,
/// then composes with <see cref="SiteMap.Compose"/>.
/// </summary>
public sealed class UrlBuilder(CmsDatabase db, CmsModel model)
{
    /// <summary>Shortcuts to shortcuts are followed this far, like a chain of redirects.</summary>
    private const int MaxLinkHops = 3;

    private readonly Dictionary<int, ContentHeader> _headers = [];
    private readonly Dictionary<Guid, int> _ids = [];

    public void Remember(IEnumerable<ContentHeader> headers)
    {
        foreach (var header in headers)
        {
            _headers[header.Id] = header;
            _ids[header.Guid] = header.Id;
        }
    }

    /// <summary>
    /// Loads the ancestors of <paramref name="items"/>, and the pages their shortcuts point at, so <see cref="UrlOf"/>
    /// can run without queries.
    /// </summary>
    public async Task PrepareAsync(IEnumerable<ContentHeader> items, CancellationToken cancellationToken)
    {
        var list = items.ToList();
        for (var hop = 0; hop <= MaxLinkHops && list.Count > 0; hop++)
        {
            Remember(list);
            var missing = list
                .Where(IsRoutable)
                .SelectMany(h => h.AncestorIds)
                .Where(id => !_headers.ContainsKey(id))
                .Distinct()
                .ToList();
            if (missing.Count > 0)
            {
                Remember((await ContentHeaderReader.ByIdsAsync(db, missing, cancellationToken)).Values);
            }

            var targets = list
                .Where(h => model.Kind(h.TypeId).IsPage())
                .SelectMany(h => h.Languages.Values)
                .Select(row => row.Link?.Guid)
                .OfType<Guid>()
                .Where(guid => !_ids.ContainsKey(guid))
                .Distinct()
                .ToList();
            var targetIds = targets.Count == 0 ? [] : (await ContentHeaderReader.IdsByGuidsAsync(db, targets, cancellationToken)).Values.ToList();
            list = targetIds.Count == 0 ? [] : (await ContentHeaderReader.ByIdsAsync(db, targetIds, cancellationToken)).Values.ToList();
        }
    }

    /// <summary>
    /// The URL in <paramref name="language"/> (null: master), for pages, media and asset folders; null otherwise.
    /// A page whose link type is a shortcut (or a link to other content) has its target's URL, as in the CMS.
    /// </summary>
    public ContentUrl? UrlOf(ContentHeader item, LanguageBranch? language) => UrlOf(item, language, 0);

    private ContentUrl? UrlOf(ContentHeader item, LanguageBranch? language, int hops)
    {
        if (!IsRoutable(item) || item.Deleted)
        {
            return null;
        }
        var kind = model.Kind(item.TypeId);
        var effective = kind.IsPage() ? language ?? model.Language(item.MasterLanguageId) : null;

        if (kind.IsPage() && item.LanguageRow(effective?.Id)?.Link is { } link && hops < MaxLinkHops
            && _ids.TryGetValue(link.Guid, out var targetId) && _headers.TryGetValue(targetId, out var target))
        {
            var url = UrlOf(target, effective, hops + 1);
            var anchor = link.Anchor is null ? "" : "#" + link.Anchor;
            return url is null ? null : url with { Path = url.Path + anchor, Absolute = url.Absolute is null ? null : url.Absolute + anchor };
        }

        var path = item.AncestorIds.Append(item.Id).ToList();
        return model.Sites.Compose(path, id => SegmentOf(id, effective), effective, kind);
    }

    private string? SegmentOf(int id, LanguageBranch? language) =>
        _headers.TryGetValue(id, out var header) ? header.RoutingRow(language?.Id)?.UrlSegment : null;

    private bool IsRoutable(ContentHeader item) => model.Kind(item.TypeId) is var kind && (kind.IsPage() || kind is ContentKind.Media or ContentKind.Folder);
}
