// Copied into a copy of the edge-case site (CMS 12 or 13) for check-endpoints.py, which checks the agent's project
// routes. Like a site's own publishing handler, it makes and publishes a page next to every page named
// "opticli-pp-notify ..." that is published, so a project publish has something of the site's own to report in
// siteChanges. The CMS publishes a project on a background task, so this runs there. And the first time a page named
// "opticli-pp-flaky ..." is published, it fails that publish before anything is saved with the error CMS 12's task
// executor fails with when it loses its race (ObjectDisposedException), which opticli tries again.
using System.Collections.Concurrent;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCliProjectsFixture;

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class ProjectsFixture : IInitializableModule
{
    /// <summary>A published page with a name that starts with this makes a page of its own.</summary>
    public const string NotifyPrefix = "opticli-pp-notify";

    /// <summary>The name of the page it makes: this, then the published page's name.</summary>
    public const string MadePrefix = "opticli-pp-made ";

    /// <summary>A page with a name that starts with this fails its first publish, before it is saved.</summary>
    public const string FlakyPrefix = "opticli-pp-flaky";

    private static IContentRepository? _repository;

    private static readonly ConcurrentDictionary<int, bool> Failed = new();

    public void Initialize(InitializationEngine context)
    {
        _repository = context.Locate.Advanced.GetRequiredService<IContentRepository>();
        context.Locate.Advanced.GetRequiredService<IContentEvents>().PublishedContent += MakePage;
        context.Locate.Advanced.GetRequiredService<IContentEvents>().PublishingContent += FailOnce;
    }

    public void Uninitialize(InitializationEngine context)
    {
        context.Locate.Advanced.GetRequiredService<IContentEvents>().PublishedContent -= MakePage;
        context.Locate.Advanced.GetRequiredService<IContentEvents>().PublishingContent -= FailOnce;
    }

    private static void FailOnce(object? sender, ContentEventArgs e)
    {
        // A version marked ready going live: what a project publish publishes (not the page's own first publish).
        if (e.Content is PageData { Status: VersionStatus.CheckedIn } page && page.Name?.StartsWith(FlakyPrefix, StringComparison.Ordinal) == true
            && Failed.TryAdd(page.ContentLink.ID, true))
        {
            throw new ObjectDisposedException("IServiceProvider");
        }
    }

    private static void MakePage(object? sender, ContentEventArgs e)
    {
        if (_repository is null || e.Content is not PageData page || page.Name?.StartsWith(NotifyPrefix, StringComparison.Ordinal) != true)
        {
            return;
        }
        var made = _repository.GetDefault<PageData>(page.ParentLink, page.ContentTypeID);
        made.Name = MadePrefix + page.Name;
        _repository.Save(made, SaveAction.Publish | SaveAction.SkipValidation, AccessLevel.NoAccess);
    }
}
