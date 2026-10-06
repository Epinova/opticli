using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;

namespace OptiCli.Cms.Operations;

/// <param name="Name">Text the content's name contains, in any case.</param>
/// <param name="Root">Where to look below; the whole tree (the root page) when left out.</param>
/// <param name="Type">Only content of this type (name or GUID).</param>
/// <param name="Lang">The language whose names are matched; each item's master language when left out.</param>
/// <param name="Limit">At most this many matches (default 20, at most 50).</param>
internal sealed record FindRequest(string Name, string? Root = null, string? Type = null, string? Lang = null, int? Limit = null);

/// <summary>
/// Content whose name contains a text, below a root, breadth first, so the nearest matches come first. The CLI searches
/// the database; the MCP module has only the CMS, so the walk is bounded: it looks at no more than
/// <see cref="MaxVisited"/> items, and reports when it stopped early.
/// </summary>
/// <remarks>
/// It walks the tree as the caller sees it: content an editor can't read is neither listed nor walked into, as for
/// <see cref="ChildrenOperation"/>, nor counted, so <c>truncated</c> tells nothing about it. It is still loaded, so a
/// larger cap on everything loaded (<see cref="MaxLoaded"/>) stops a walk through a tree of mostly hidden content; that
/// stop also says <c>truncated</c>, the one thing a search can still tell of what an editor can't read (that there is a
/// great deal of it). The recycle bin is only searched when it is the root.
/// </remarks>
internal static class FindOperation
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 50;

    /// <summary>The most content items the caller can read that one search looks at, whatever it finds: what keeps a search on a large site cheap.</summary>
    public const int MaxVisited = 5000;

    /// <summary>The most content items one search loads, those the caller can't read included: a safety stop.</summary>
    public const int MaxLoaded = 4 * MaxVisited;

    private const int Batch = 100;

    /// <exception cref="AgentException"><c>usage</c> without a name; <c>not_found</c> for a root that doesn't exist or the caller can't read.</exception>
    public static ContentList Run(CmsCall call, FindRequest body)
    {
        var text = body.Name?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw AgentException.Usage("name is required: the text the content's name contains.");
        }
        var locator = new ContentLocator(call);
        var rootLink = body.Root is null ? ContentReference.RootPage : locator.ResolveContent(body.Root, "root");
        var root = locator.LoadAnyLanguage(rootLink);
        var types = call.Service<IContentTypeRepository>();
        var type = body.Type is null ? null : TypeOperation.Find(call, types, body.Type);
        var limit = Paging.Limit(body.Limit, DefaultLimit, MaxLimit);
        var options = Paging.Language(body.Lang);
        var loader = call.Service<IContentLoader>();

        var found = new List<Protocol.ContentSummary>();
        var queue = new Queue<ContentReference>([rootLink]);
        var visited = 0;
        var loaded = 0;
        var truncated = false;
        while (queue.Count > 0 && !truncated)
        {
            var parent = queue.Dequeue();
            for (var start = 0; !truncated; start += Batch)
            {
                var batch = loader.GetChildren<IContent>(parent, options, start, Batch).ToList();
                foreach (var child in batch)
                {
                    if (loaded == MaxLoaded)
                    {
                        truncated = true;
                        break;
                    }
                    loaded++;
                    if (!call.CanRead(child) || child.ContentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket))
                    {
                        continue;
                    }
                    // Only now, with an item the caller can read left to look at, is the search cut short.
                    if (found.Count == limit || visited == MaxVisited)
                    {
                        truncated = true;
                        break;
                    }
                    visited++;
                    // Real sites have content without a name (e.g. from a content provider, or a branch missing in this language).
                    if (child.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true && (type is null || child.ContentTypeID == type.ID))
                    {
                        found.Add(ContentSummaries.Describe(child, types));
                    }
                    queue.Enqueue(child.ContentLink.ToReferenceWithoutVersion());
                }
                if (batch.Count < Batch)
                {
                    break;
                }
            }
        }
        // Stopping exactly when the walk ran out is complete, not truncated.
        return new ContentList(found, null) { Of = ContentSummaries.Describe(root, types), Truncated = truncated };
    }
}
