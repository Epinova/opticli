using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;

namespace OptiCli.Cms.Operations;

/// <param name="Lang">The language to list names in; each child's master language when left out.</param>
/// <param name="Cursor">Where to start: 0, or the previous page's <see cref="ContentList.Next"/>.</param>
/// <param name="Limit">The page size (default 50, at most 200).</param>
internal sealed record ChildrenRequest(string? Lang = null, int? Cursor = null, int? Limit = null);

/// <summary>
/// The children of a page or folder, in the parent's sort order. The CLI reads these from the database; the MCP module
/// has only the CMS, and an editor sees only the children they can read.
/// </summary>
internal static class ChildrenOperation
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    /// <summary>How many children are loaded at a time while skipping those the caller can't read.</summary>
    private const int Batch = 100;

    /// <exception cref="AgentException"><c>not_found</c> for a parent that doesn't exist or the caller can't read.</exception>
    public static ContentList Run(CmsCall call, string reference, ChildrenRequest body)
    {
        var locator = new ContentLocator(call);
        var link = locator.ResolveContent(reference);
        var parent = locator.LoadAnyLanguage(link);
        var limit = Paging.Limit(body.Limit, DefaultLimit, MaxLimit);
        var index = Paging.Cursor(body.Cursor);
        var options = Paging.Language(body.Lang);
        var loader = call.Service<IContentLoader>();
        var types = call.Service<IContentTypeRepository>();

        // The cursor counts every child, readable or not, so a page never depends on what earlier pages left out.
        var items = new List<Protocol.ContentSummary>();
        var more = true;
        while (more && items.Count < limit)
        {
            var batch = loader.GetChildren<IContent>(link, options, index, Batch).ToList();
            // A full batch may have more after it; a short one was the last.
            more = batch.Count == Batch;
            for (var i = 0; i < batch.Count && items.Count < limit; i++)
            {
                index++;
                if (call.CanRead(batch[i]))
                {
                    items.Add(ContentSummaries.Describe(batch[i], types));
                }
                if (items.Count == limit && i < batch.Count - 1)
                {
                    more = true;
                }
            }
        }
        return new ContentList(items, more ? index : null) { Of = ContentSummaries.Describe(parent, types) };
    }
}
