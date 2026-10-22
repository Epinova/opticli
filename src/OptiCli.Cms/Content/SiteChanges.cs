using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>Something the CMS's content events reported during a save: <see cref="SiteChanges"/>.</summary>
/// <param name="Content">The content as the event gave it, if it did.</param>
/// <param name="From">For a move: the parent it was moved from.</param>
internal sealed record SiteEvent(ContentReference Link, IContent? Content, string Action, ContentReference? From = null);

/// <summary>
/// What the site's own code changed while a save ran: handlers of the CMS's content events may create, publish or move
/// other content, or put the saved content somewhere else than it was asked to go (a site that files articles into
/// year folders, say). A dry run doesn't save, so it can't run them; the result of the real save reports what they did,
/// as <see cref="WriteResult.SiteChanges"/>.
/// </summary>
/// <remarks>
/// The CMS raises its events on the thread that saves, inside the save. The handlers are added to the site-wide
/// <see cref="IContentEvents"/> for as long as the save runs, and only record events of this save's own flow
/// (<see cref="Current"/>), so another request's saves at the same time aren't taken for this one's.
/// </remarks>
internal sealed class SiteChanges : IDisposable
{
    private static readonly AsyncLocal<SiteChanges?> Current = new();

    private readonly IContentEvents _events;

    private readonly List<SiteEvent> _into;

    private readonly SiteChanges? _outer;

    private SiteChanges(IContentEvents events, List<SiteEvent> into)
    {
        _events = events;
        _into = into;
        _outer = Current.Value;
        Current.Value = this;
        events.CreatedContent += OnCreated;
        events.CreatedContentLanguage += OnCreated;
        events.SavedContent += OnSaved;
        events.PublishedContent += OnPublished;
        events.MovedContent += OnMoved;
        events.DeletedContent += OnDeleted;
    }

    /// <summary>Records the content events of the current flow into <paramref name="into"/> until disposed; nothing without <paramref name="events"/>.</summary>
    public static IDisposable? Record(IContentEvents? events, List<SiteEvent> into) =>
        events is null ? null : new SiteChanges(events, into);

    public void Dispose()
    {
        _events.CreatedContent -= OnCreated;
        _events.CreatedContentLanguage -= OnCreated;
        _events.SavedContent -= OnSaved;
        _events.PublishedContent -= OnPublished;
        _events.MovedContent -= OnMoved;
        _events.DeletedContent -= OnDeleted;
        Current.Value = _outer;
    }

    private void OnCreated(object? sender, ContentEventArgs e) => Add(e, "created");

    private void OnSaved(object? sender, ContentEventArgs e) => Add(e, "saved");

    private void OnPublished(object? sender, ContentEventArgs e) => Add(e, "published");

    private void OnDeleted(object? sender, ContentEventArgs e) => Add(e, "deleted");

    /// <summary>A move to the recycle bin is how the CMS deletes.</summary>
    private void OnMoved(object? sender, ContentEventArgs e) =>
        Add(e, e.TargetLink.CompareToIgnoreWorkID(ContentReference.WasteBasket) ? "deleted" : "moved", (e as MoveContentEventArgs)?.OriginalParent);

    private void Add(ContentEventArgs e, string action, ContentReference? from = null)
    {
        if (Current.Value == this && !ContentReference.IsNullOrEmpty(e.ContentLink))
        {
            _into.Add(new SiteEvent(e.ContentLink, e.Content, action, from));
        }
    }

    /// <summary>
    /// The changes to report for a save of <paramref name="saved"/>: everything the events say happened to other content,
    /// and of the saved content itself only what the save didn't ask for (moved, deleted, or published by a save that
    /// didn't publish). Content the caller can't read is left out.
    /// </summary>
    /// <param name="askedParent">For new content, the parent it was asked to go below: reported as moved from there when the site put it elsewhere.</param>
    /// <returns>Null when there is nothing to report.</returns>
    public static IReadOnlyList<SiteChange>? Report(CmsCall call, IContentTypeRepository types, IReadOnlyList<SiteEvent> events, IContent saved, ContentReference? askedParent, bool published)
    {
        var id = saved.ContentLink.ID;
        var own = new HashSet<string> { "moved", "deleted" };
        if (!published)
        {
            own.Add("published");
        }
        var changes = new List<SiteChange>();
        var savedChange = Change(call, types, events.Where(e => e.Link.ID == id && own.Contains(e.Action)).ToList(), saved);
        if (savedChange is null && askedParent is not null && !askedParent.CompareToIgnoreWorkID(saved.ParentLink) && call.CanRead(saved))
        {
            savedChange = new SiteChange(ContentSummaries.Describe(saved, types), ["moved"], askedParent.ToReferenceWithoutVersion().ToString());
        }
        if (savedChange is not null)
        {
            changes.Add(savedChange);
        }
        foreach (var group in events.Where(e => e.Link.ID != id).GroupBy(e => e.Link.ID))
        {
            if (Change(call, types, group.ToList(), null) is { } change)
            {
                changes.Add(change);
            }
        }
        return changes.Count > 0 ? changes : null;
    }

    /// <summary>One content item's events as one change, described as it is now (else as the last event gave it).</summary>
    private static SiteChange? Change(CmsCall call, IContentTypeRepository types, IReadOnlyList<SiteEvent> events, IContent? content)
    {
        if (events.Count == 0)
        {
            return null;
        }
        var last = events[^1];
        if (content is null && !call.Service<IContentRepository>().TryGet(last.Link, out content))
        {
            content = events.Select(e => e.Content).LastOrDefault(c => c is not null);
        }
        if (content is null || !call.CanRead(content))
        {
            return null;
        }
        var from = events.LastOrDefault(e => e.From is not null)?.From;
        return new SiteChange(
            ContentSummaries.Describe(content, types),
            events.Select(e => e.Action).Distinct().ToList(),
            from is null || ContentReference.IsNullOrEmpty(from) ? null : from.ToReferenceWithoutVersion().ToString());
    }
}
