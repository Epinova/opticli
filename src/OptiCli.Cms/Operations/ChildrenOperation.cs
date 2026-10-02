using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;

namespace OptiCli.Cms.Operations;

/// <param name="Lang">The language to list names in; each child's master language when left out.</param>
/// <param name="Cursor">Where to start: 0, or the previous page's <see cref="ContentList.Next"/>, counted in children the caller can read.</param>
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

    /// <summary>How many children are loaded at a time while counting those the caller can read.</summary>
    private const int Batch = 100;

    /// <exception cref="AgentException"><c>not_found</c> for a parent that doesn't exist or the caller can't read.</exception>
    public static ContentList Run(CmsCall call, string reference, ChildrenRequest body)
    {
        var locator = new ContentLocator(call);
        var link = locator.ResolveContent(reference);
        var parent = locator.LoadAnyLanguage(link);
        var limit = Paging.Limit(body.Limit, DefaultLimit, MaxLimit);
        var cursor = Paging.Cursor(body.Cursor);
        var options = Paging.Language(body.Lang);
        var loader = call.Service<IContentLoader>();
        var types = call.Service<IContentTypeRepository>();

        // The cursor counts only children the caller can read, so it says nothing about the others (a count of every
        // child would tell how many hidden ones lie between two pages). Each page therefore reads from the first child.
        var items = new List<Protocol.ContentSummary>();
        var skip = cursor;
        var more = false;
        for (var start = 0; ; start += Batch)
        {
            var batch = loader.GetChildren<IContent>(link, options, start, Batch).ToList();
            foreach (var child in batch.Where(call.CanRead))
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                if (items.Count == limit)
                {
                    more = true;
                    break;
                }
                items.Add(ContentSummaries.Describe(child, types));
            }
            // A short batch was the last.
            if (more || batch.Count < Batch)
            {
                break;
            }
        }
        return new ContentList(items, more ? cursor + items.Count : null) { Of = ContentSummaries.Describe(parent, types) };
    }
}
