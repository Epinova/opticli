using System.Security.Principal;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// What the site's own code did during a save, from the CMS's content events: a site that files a new article into a
/// year folder it creates and publishes, as a tester's assistant met.
/// </summary>
public class SiteChangesTests
{
    private static readonly ContentReference Blog = new(5);

    private static readonly CmsCallTests.Secured YearFolder = new(new ContentReference(20, 7), AccessLevel.Read) { Name = "2026", ParentLink = Blog };

    private static readonly CmsCallTests.Secured Article = new(new ContentReference(30, 9), AccessLevel.Read) { Name = "Release notes", ParentLink = new ContentReference(20) };

    [Fact]
    public void A_new_article_filed_into_a_new_folder_reports_the_folder_and_where_the_article_went()
    {
        var (events, raise) = Events();
        var recorded = new List<SiteEvent>();
        using (SiteChanges.Record(events, recorded))
        {
            raise("CreatedContent", new ContentEventArgs(YearFolder.ContentLink, YearFolder));
            raise("PublishedContent", new ContentEventArgs(YearFolder.ContentLink, YearFolder));
            raise("CreatedContent", new ContentEventArgs(Article.ContentLink, Article));
        }

        var changes = SiteChanges.Report(Call(CmsCaller.Editor), Types(), recorded, Article, askedParent: Blog, published: false)!;

        Assert.Equal(2, changes.Count);
        Assert.Equal((30, "moved", "5"), (changes[0].Content.Id, string.Join(",", changes[0].Actions), changes[0].PreviousParent));
        Assert.Equal(("2026", "created,published", "5"), (changes[1].Content.Name, string.Join(",", changes[1].Actions), changes[1].Content.Parent));
    }

    [Fact]
    public void The_saves_own_events_are_not_site_changes()
    {
        var (events, raise) = Events();
        var recorded = new List<SiteEvent>();
        using (SiteChanges.Record(events, recorded))
        {
            raise("CreatedContent", new ContentEventArgs(Article.ContentLink, Article));
            raise("PublishedContent", new ContentEventArgs(Article.ContentLink, Article));
        }

        Assert.Null(SiteChanges.Report(Call(CmsCaller.Editor), Types(), recorded, Article, askedParent: Article.ParentLink, published: true));
    }

    [Fact]
    public void Content_the_editor_cant_read_is_left_out()
    {
        var hidden = new CmsCallTests.Secured(new ContentReference(40), AccessLevel.NoAccess) { Name = "Audit log" };
        var (events, raise) = Events();
        var recorded = new List<SiteEvent>();
        using (SiteChanges.Record(events, recorded))
        {
            raise("SavedContent", new ContentEventArgs(hidden.ContentLink, hidden));
        }

        Assert.Null(SiteChanges.Report(Call(CmsCaller.Editor), Types(), recorded, Article, askedParent: null, published: false));
        Assert.NotNull(SiteChanges.Report(Call(CmsCaller.Developer), Types(), recorded, Article, askedParent: null, published: false));
    }

    [Fact]
    public void Only_events_while_recording_and_in_its_own_flow_count()
    {
        var (events, raise) = Events();
        var recorded = new List<SiteEvent>();
        using (SiteChanges.Record(events, recorded))
        {
            // Another request saving at the same time: its own flow, without this recording.
            var other = new Thread(() => raise("SavedContent", new ContentEventArgs(YearFolder.ContentLink, YearFolder)));
            other.UnsafeStart();
            other.Join();
            raise("MovedContent", new MoveContentEventArgs(YearFolder.ContentLink, ContentReference.WasteBasket));
        }
        raise("SavedContent", new ContentEventArgs(YearFolder.ContentLink, YearFolder));

        Assert.Equal("deleted", Assert.Single(recorded).Action);
    }

    /// <summary>Content events whose handlers are kept, and a way to raise one.</summary>
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

    private static IContentTypeRepository Types() => Recorder<IContentTypeRepository>.Create().Proxy;

    /// <summary>A call whose repository has nothing, so changes are described as the events gave the content.</summary>
    private static CmsCall Call(CmsCaller caller)
    {
        var services = new ServiceCollection()
            .AddSingleton(Recorder<IContentRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor@example.com"), [])))
            .BuildServiceProvider();
        return new CmsCall(services, CancellationToken.None, caller);
    }
}
