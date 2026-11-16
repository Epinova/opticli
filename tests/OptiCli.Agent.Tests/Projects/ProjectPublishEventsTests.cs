using System.Security.Principal;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Cms;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Cms.Projects;

namespace OptiCli.Agent.Tests.Projects;

/// <summary>
/// What the site's code did during a project publish, which the CMS runs on a background task of its own: the events on
/// the thread that publishes the items, until the project's final status is saved.
/// </summary>
public class ProjectPublishEventsTests
{
    private static readonly ContentReference ItemA = new(10, 100);

    private static readonly ContentReference ItemB = new(11, 110);

    private static readonly CmsCallTests.Secured Made = new(new ContentReference(50, 500), AccessLevel.Read) { Name = "Made by the site", ParentLink = new ContentReference(5) };

    private static readonly CmsCallTests.Secured Colleague = new(new ContentReference(60, 600), AccessLevel.Read) { Name = "A colleague's page" };

    [Fact]
    public void Only_the_publish_threads_events_count_from_its_first_item_until_the_projects_final_status()
    {
        var (events, raise) = Events();
        var projects = new FakeProjects();
        var project = projects.Add(901, "Launch");
        ProjectPublishEvents? recording;
        using (recording = ProjectPublishEvents.Record(events, project.ID, [ItemA, ItemB]))
        {
            // Before the publish starts, and a colleague's save on another thread while it runs.
            raise("SavedContent", new ContentEventArgs(Colleague.ContentLink, Colleague));
            OnThread(() =>
            {
                raise("SavedContent", new ContentEventArgs(Colleague.ContentLink, Colleague));
                // Another project's status saved on this thread doesn't end the recording.
                projects.RaiseSaved(new Project { ID = 902, Name = "Other", Status = ProjectStatus.Published });
            });
            OnThread(() =>
            {
                raise("SavingContent", new ContentEventArgs(ItemA));
                raise("SavedContent", new ContentEventArgs(ItemA));
                raise("PublishedContent", new ContentEventArgs(ItemA));
                // A site handler of the publish makes and publishes a page.
                raise("CreatedContent", new ContentEventArgs(Made.ContentLink, Made));
                raise("PublishedContent", new ContentEventArgs(Made.ContentLink, Made));
                raise("SavingContent", new ContentEventArgs(ItemB));
                raise("PublishedContent", new ContentEventArgs(ItemB));
                projects.RaiseSaved(new Project { ID = 901, Name = "Launch", Status = ProjectStatus.Published });
                // The thread back in the pool, running someone else's save.
                raise("SavedContent", new ContentEventArgs(Colleague.ContentLink, Colleague));
            });
        }

        Assert.Equal(["10:saved", "10:published", "50:created", "50:published", "11:published"], recording!.Events.Select(e => $"{e.Link.ID}:{e.Action}"));

        var changes = SiteChanges.ReportProjectPublish(Call(), Recorder<IContentTypeRepository>.Create().Proxy, recording.Events, new HashSet<int> { 10, 11 });
        var change = Assert.Single(changes!);
        Assert.Equal(("Made by the site", "created,published"), (change.Content.Name, string.Join(",", change.Actions)));
    }

    [Fact]
    public void A_publish_whose_first_item_never_starts_records_nothing()
    {
        var (events, raise) = Events();
        using var recording = ProjectPublishEvents.Record(events, 903, [ItemA])!;
        OnThread(() => raise("SavingContent", new ContentEventArgs(Colleague.ContentLink, Colleague)));
        OnThread(() => raise("SavedContent", new ContentEventArgs(Colleague.ContentLink, Colleague)));

        Assert.Empty(recording.Events);
    }

    [Fact]
    public void An_item_the_site_moves_is_reported_but_not_its_own_publish()
    {
        var events = new List<SiteEvent>
        {
            new(ItemA, null, "published"),
            new(ItemA, null, "moved", new ContentReference(5)),
        };
        var item = new CmsCallTests.Secured(ItemA, AccessLevel.Read) { Name = "Moved by the site", ParentLink = new ContentReference(7) };
        var (repository, loaded) = Recorder<IContentRepository>.Create();
        loaded.Answer = (method, args) =>
        {
            if (method.Name == nameof(IContentLoader.TryGet) && args.Length == 2)
            {
                args[1] = item;
                return true;
            }
            return null;
        };

        var change = Assert.Single(SiteChanges.ReportProjectPublish(Call(repository), Recorder<IContentTypeRepository>.Create().Proxy, events, new HashSet<int> { 10 })!);

        Assert.Equal(("moved", "5"), (string.Join(",", change.Actions), change.PreviousParent));
    }

    private static void OnThread(Action action)
    {
        var thread = new Thread(() => action());
        thread.UnsafeStart();
        thread.Join();
    }

    private static CmsCall Call(IContentRepository? repository = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(repository ?? Recorder<IContentRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor@example.com"), [])))
            .BuildServiceProvider();
        return new CmsCall(services, CancellationToken.None, CmsCaller.Developer);
    }

    /// <summary>Content events whose handlers are kept, and a way to raise one (as SiteChangesTests has them).</summary>
    private static (IContentEvents Events, Action<string, ContentEventArgs> Raise) Events()
    {
        var handlers = new Dictionary<string, Delegate?>();
        var (events, recorder) = Recorder<IContentEvents>.Create();
        recorder.Answer = (method, args) =>
        {
            lock (handlers)
            {
                if (method.Name.StartsWith("add_", StringComparison.Ordinal))
                {
                    handlers[method.Name[4..]] = Delegate.Combine(handlers.GetValueOrDefault(method.Name[4..]), (Delegate)args[0]!);
                }
                else if (method.Name.StartsWith("remove_", StringComparison.Ordinal))
                {
                    handlers[method.Name[7..]] = Delegate.Remove(handlers.GetValueOrDefault(method.Name[7..]), (Delegate)args[0]!);
                }
            }
            return null;
        };
        return (events, (name, e) =>
        {
            Delegate? handler;
            lock (handlers)
            {
                handler = handlers.GetValueOrDefault(name);
            }
            handler?.DynamicInvoke(null, e);
        });
    }
}
