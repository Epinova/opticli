using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;

namespace OptiCli.Cms.Projects;

/// <summary>
/// What the site's own code does while the CMS publishes a project: <see cref="SiteChanges"/>, for a publish that runs on
/// the CMS's own background task. The events are told apart by the thread they are raised on.
/// </summary>
/// <remarks>
/// <para><c>ProjectPublisher</c> publishes through <c>BatchPublisher</c> on a <c>TaskExecutor</c> task. On CMS 13 that task
/// doesn't carry the request's execution context (only the principal), so <see cref="SiteChanges"/>' flow-bound
/// recording sees nothing of it. <c>BatchPublisher</c> publishes every item one after the other on that one thread, and
/// the CMS raises its content events synchronously on it, site handlers included, so nothing else runs there meanwhile:
/// the events raised on the thread of the first item's <c>SavingContent</c> are the publish's own and its handlers'
/// (checked on CMS 12 and 13 with a colleague saving every 100 ms on another thread: none of theirs got in).</para>
/// <para>Recording stops when the CMS saves the project's final status (published, scheduled or failed), after the last
/// item, so the thread isn't followed once it goes back to the pool. What a site's handler hands to yet another thread,
/// or awaits and resumes elsewhere, is missed; it is never taken from someone else's save. The handlers are on the
/// site-wide <see cref="IContentEvents"/> for as long as the publish runs, so every save on the site passes them: the
/// thread check is all they do for those.</para>
/// </remarks>
internal sealed class ProjectPublishEvents : IDisposable
{
    private readonly IContentEvents _events;

    private readonly int _project;

    private readonly IReadOnlyCollection<ContentReference> _items;

    private readonly List<SiteEvent> _recorded = [];

    private readonly object _lock = new();

    private int? _thread;

    private bool _done;

    private ProjectPublishEvents(IContentEvents events, int project, IReadOnlyCollection<ContentReference> items)
    {
        _events = events;
        _project = project;
        _items = items;
        events.SavingContent += OnSaving;
        events.CreatedContent += OnCreated;
        events.CreatedContentLanguage += OnCreated;
        events.SavedContent += OnSaved;
        events.PublishedContent += OnPublished;
        events.MovedContent += OnMoved;
        events.DeletedContent += OnDeleted;
        ProjectRepository.ProjectSaved += OnProjectSaved;
    }

    /// <summary>Records the publish of <paramref name="items"/> (their versions) in <paramref name="project"/> until disposed; nothing without <paramref name="events"/>.</summary>
    public static ProjectPublishEvents? Record(IContentEvents? events, int project, IEnumerable<ContentReference> items) =>
        events is null ? null : new ProjectPublishEvents(events, project, items.ToList());

    /// <summary>The events recorded so far, in order.</summary>
    public IReadOnlyList<SiteEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _recorded.ToList();
            }
        }
    }

    public void Dispose()
    {
        _events.SavingContent -= OnSaving;
        _events.CreatedContent -= OnCreated;
        _events.CreatedContentLanguage -= OnCreated;
        _events.SavedContent -= OnSaved;
        _events.PublishedContent -= OnPublished;
        _events.MovedContent -= OnMoved;
        _events.DeletedContent -= OnDeleted;
        ProjectRepository.ProjectSaved -= OnProjectSaved;
    }

    /// <summary>The first item's <c>SavingContent</c> names the publish's thread.</summary>
    private void OnSaving(object? sender, ContentEventArgs e)
    {
        lock (_lock)
        {
            if (_thread is null && !_done && IsItem(e.ContentLink))
            {
                _thread = Environment.CurrentManagedThreadId;
            }
        }
    }

    private void OnProjectSaved(object? sender, ProjectEventArgs e)
    {
        if (e.Project is { } project && project.ID == _project
            && project.Status is ProjectStatus.Published or ProjectStatus.DelayedPublished or ProjectStatus.PublishFailed)
        {
            lock (_lock)
            {
                _done = true;
            }
        }
    }

    private void OnCreated(object? sender, ContentEventArgs e) => Add(e, "created");

    private void OnSaved(object? sender, ContentEventArgs e) => Add(e, "saved");

    private void OnPublished(object? sender, ContentEventArgs e) => Add(e, "published");

    private void OnDeleted(object? sender, ContentEventArgs e) => Add(e, "deleted");

    private void OnMoved(object? sender, ContentEventArgs e) =>
        Add(e, e.TargetLink.CompareToIgnoreWorkID(ContentReference.WasteBasket) ? "deleted" : "moved", (e as MoveContentEventArgs)?.OriginalParent);

    private void Add(ContentEventArgs e, string action, ContentReference? from = null)
    {
        if (ContentReference.IsNullOrEmpty(e.ContentLink))
        {
            return;
        }
        lock (_lock)
        {
            if (!_done && _thread == Environment.CurrentManagedThreadId)
            {
                _recorded.Add(new SiteEvent(e.ContentLink, e.Content, action, from));
            }
        }
    }

    /// <summary>One of the items' versions (the version itself when the event names one).</summary>
    private bool IsItem(ContentReference? link) =>
        !ContentReference.IsNullOrEmpty(link)
        && _items.Any(i => link!.WorkID > 0 ? i.Equals(link) : i.CompareToIgnoreWorkID(link));
}
