using System.Runtime.CompilerServices;
using EPiServer;
using EPiServer.Construction;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Validation;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// The publishing gate (the MCP module's AllowPublish and content:publish) must stop a save even where a failed save
/// is otherwise taken for "saved after all": WriteFlow's catch looks for a version newer than before the save, and
/// finds one when someone else saved meanwhile.
/// </summary>
public class PublishGateTests
{
    private static readonly ContentReference Page = new(123);

    [Fact]
    public void A_refused_publish_is_never_reported_as_saved_and_nothing_is_saved_or_restored_first()
    {
        var (repository, saves) = Recorder<IContentRepository>.Create();
        var (versions, listed) = Recorder<IContentVersionRepository>.Create();
        // Every listing finds one more version than the last, so the catch would always find a "newer" one.
        var newest = 5;
        listed.Answer = (method, args) =>
        {
            if (method.Name != nameof(IContentVersionRepository.List))
            {
                return null;
            }
            args[^1] = 1; // the total count, an out parameter
            return new[] { Version(newest++) };
        };
        var (validation, validated) = Recorder<IValidationService>.Create();
        validated.Answer = (method, _) => method.Name == nameof(IValidationService.Validate) ? Array.Empty<ValidationError>() : null;
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(versions)
            .AddSingleton(validation)
            .AddSingleton(Recorder<IContentTypeRepository>.Create().Proxy)
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton(Recorder<IContentDataFactory<BlockData>>.Create().Proxy)
            .AddSingleton(Recorder<IContentDataBuilder>.Create().Proxy)
            .AddSingleton(Recorder<IFrameRepository>.Create().Proxy)
            .AddSingleton(Unused<CategoryRepository>())
            .AddSingleton(Unused<EPiServer.Web.DisplayOptions>())
            .BuildServiceProvider();
        var call = new CmsCall(services, CancellationToken.None, CmsCaller.Editor, () => throw new GateRefused());
        var flow = new WriteFlow(call);
        var page = new VersionedPage();
        var restored = false;
        saves.Answer = (method, _) => method.Name switch
        {
            nameof(IContentRepository.Save) => Page,
            nameof(IContentLoader.Get) => page,
            _ => null,
        };

        foreach (var action in new[] { SaveAction.Publish | SaveAction.ForceNewVersion, SaveAction.Schedule | SaveAction.ForceNewVersion })
        {
            Assert.Throws<GateRefused>(() => flow.Save(page, PropertyValues.Snapshot(page), action, dryRun: false, shown: null, baseVersion: null,
                saveUnchanged: true, beforeSave: () => restored = true));
        }

        Assert.DoesNotContain(saves.Calls, c => c.Method == nameof(IContentRepository.Save));
        Assert.False(restored);
        // A draft passes the gate (and is saved).
        flow.Save(page, PropertyValues.Snapshot(page), SaveAction.Save | SaveAction.ForceNewVersion, dryRun: false, shown: null, baseVersion: null, saveUnchanged: true);
        Assert.Contains(saves.Calls, c => c.Method == nameof(IContentRepository.Save));
    }

    /// <summary>An instance of <typeparamref name="T"/> (or the CMS's own subclass of it) for a constructor that only keeps it.</summary>
    private static T Unused<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T).IsAbstract
            ? typeof(T).Assembly.GetTypes().First(t => t.IsSubclassOf(typeof(T)) && !t.IsAbstract)
            : typeof(T));

    private static ContentVersion Version(int id) =>
        new(new ContentReference(123, id), "News", VersionStatus.CheckedOut, DateTime.UtcNow, "someone", "someone", 0, "en", true, false);

    private sealed class GateRefused() : Exception("publishing off");

    private sealed class VersionedPage : IContent, IVersionable
    {
        public PropertyDataCollection Property { get; } = new();

        public string Name { get; set; } = "News";

        public ContentReference ContentLink { get; set; } = Page;

        public ContentReference ParentLink { get; set; } = ContentReference.EmptyReference;

        public Guid ContentGuid { get; set; } = Guid.NewGuid();

        public int ContentTypeID { get; set; } = 1;

        public bool IsDeleted { get; set; }

        public bool IsPendingPublish { get; set; }

        public DateTime? StartPublish { get; set; }

        public DateTime? StopPublish { get; set; }

        public VersionStatus Status { get; set; } = VersionStatus.CheckedOut;
    }
}
