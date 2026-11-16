using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Cms;
using OptiCli.Cms;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Projects;

/// <summary>
/// A save into a project: one version per content item and language (the newer replaces the older), not content another
/// project has, and the request rules around it.
/// </summary>
public class ProjectSaveTests
{
    private readonly FakeProjects _projects = new();

    private readonly Recorder<IContentRepository> _saves;

    private readonly CmsCall _call;

    public ProjectSaveTests()
    {
        _projects.Add(1, "Launch");
        var (repository, saves) = Recorder<IContentRepository>.Create();
        _saves = saves;
        var services = new ServiceCollection()
            .AddSingleton<ProjectRepository>(_projects)
            .AddSingleton(repository)
            .BuildServiceProvider();
        _call = new CmsCall(services, CancellationToken.None, CmsCaller.Developer);
    }

    private ProjectSave Save(string project = "Launch", bool ready = false) => ProjectSave.For(_call, project, ready, publish: false, publishAt: null)!;

    private static Page Version(int id, int version, string? language = "en", VersionStatus status = VersionStatus.CheckedOut) =>
        new(new ContentReference(id, version), language is null ? null : CultureInfo.GetCultureInfo(language)) { Status = status };

    [Fact]
    public void A_save_without_project_has_none_and_ready_needs_one()
    {
        Assert.Null(ProjectSave.For(_call, null, ready: false, publish: true, publishAt: null));
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => ProjectSave.For(_call, null, ready: true, publish: false, publishAt: null)).Code);
    }

    [Fact]
    public void A_save_into_a_project_neither_publishes_nor_schedules()
    {
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => ProjectSave.For(_call, "1", false, publish: true, publishAt: null)).Code);
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => ProjectSave.For(_call, "1", false, publish: false, publishAt: DateTime.UtcNow.AddDays(1))).Code);
    }

    [Fact]
    public void Only_an_active_project_takes_saves()
    {
        _projects.Add(2, "Done", ProjectStatus.Published);

        var error = Assert.Throws<AgentException>(() => Save("Done"));

        Assert.Equal(AgentErrorReasons.ProjectNotActive, error.Reason);
        Assert.Equal(AgentErrorCodes.NotFound, Assert.Throws<AgentException>(() => Save("Nope")).Code);
    }

    [Fact]
    public void The_first_version_is_added_as_the_edit_ui_adds_it()
    {
        var result = Save().Add(Version(10, 100), SaveAction.Save | SaveAction.ForceNewVersion);

        Assert.Equal((1, "Launch", ProjectSaveItems.Added, (string?)null, false, (string?)null), (result.Id, result.Name, result.Item, result.Replaced, result.Ready, result.Error));
        var item = Assert.Single(_projects.Items);
        Assert.Equal((1, "10_100", "en", "default"), (item.ProjectID, item.ContentLink.ToString(), item.Language.Name, item.Category));
    }

    [Fact]
    public void A_newer_version_replaces_the_older_in_the_same_language_only()
    {
        _projects.AddItem(1, new ContentReference(10, 100), "en");
        _projects.AddItem(1, new ContentReference(10, 90), "sv");
        _projects.AddItem(1, new ContentReference(11, 110), "en");

        var result = Save().Add(Version(10, 105), SaveAction.Save);

        Assert.Equal((ProjectSaveItems.Replaced, "10_100"), (result.Item, result.Replaced));
        Assert.Equal(["10_105:en", "10_90:sv", "11_110:en"], _projects.Items.Select(i => $"{i.ContentLink}:{i.Language.Name}").Order());
    }

    [Fact]
    public void Content_without_a_language_is_an_item_in_the_invariant_culture()
    {
        _projects.AddItem(1, new ContentReference(12, 120), null);

        var result = Save().Add(Version(12, 121, language: null), SaveAction.Save);

        Assert.Equal(ProjectSaveItems.Replaced, result.Item);
        Assert.Equal(CultureInfo.InvariantCulture, Assert.Single(_projects.Items).Language);
    }

    [Fact]
    public void The_version_the_project_holds_is_left_as_it_is()
    {
        var held = _projects.AddItem(1, new ContentReference(10, 100), "en");

        var result = Save().Add(Version(10, 100), SaveAction.Save | SaveAction.ForceCurrentVersion);

        Assert.Equal(ProjectSaveItems.Unchanged, result.Item);
        Assert.Same(held, Assert.Single(_projects.Items));
    }

    [Fact]
    public void Ready_checks_the_saved_version_in_as_it_is()
    {
        var result = Save(ready: true).Add(Version(10, 100), SaveAction.Save | SaveAction.ForceNewVersion);

        Assert.True(result.Ready);
        var (_, args) = Assert.Single(_saves.Calls, c => c.Method == nameof(IContentRepository.Save));
        Assert.Equal((new ContentReference(10, 100), SaveAction.CheckIn, AccessLevel.NoAccess), (((IContent)args[0]!).ContentLink, (SaveAction)args[1]!, (AccessLevel)args[2]!));
    }

    [Fact]
    public void A_version_sent_for_review_isnt_checked_in()
    {
        var result = Save(ready: true).Add(Version(10, 100, status: VersionStatus.AwaitingApproval), SaveAction.RequestApproval | SaveAction.ForceNewVersion);

        Assert.False(result.Ready);
        Assert.DoesNotContain(_saves.Calls, c => c.Method == nameof(IContentRepository.Save));
    }

    [Fact]
    public void A_failure_after_the_save_is_reported_and_the_save_stands()
    {
        _saves.Answer = (method, _) => method.Name == nameof(IContentRepository.Save) ? throw new InvalidOperationException("The site refused it.") : null;

        var result = Save(ready: true).Add(Version(10, 100), SaveAction.Save);

        Assert.Equal((ProjectSaveItems.Added, false), (result.Item, result.Ready));
        Assert.Contains("The site refused it.", result.Error);
    }

    [Fact]
    public void Content_in_another_unpublished_project_is_refused_in_its_language()
    {
        _projects.Add(2, "Other");
        _projects.Add(3, "Old", ProjectStatus.Published);
        _projects.AddItem(2, new ContentReference(10, 90), "sv");
        _projects.AddItem(3, new ContentReference(10, 80), "en");
        var save = Save();

        // Another language in another project, and the same language in a published one, are no conflict.
        save.Check(Version(10, 100));

        _projects.AddItem(2, new ContentReference(10, 95), "en");
        var error = Assert.Throws<AgentException>(() => save.Check(Version(10, 100)));
        Assert.Equal((AgentErrorCodes.Conflict, AgentErrorReasons.InOtherProject), (error.Code, error.Reason));
        Assert.Contains("2 ('Other', active)", error.Message);
        Assert.Contains("10_95", error.Message);
        Assert.Contains("opticli projects remove 2 10", error.Hint);
    }

    [Fact]
    public void Folders_and_content_without_versions_cant_be_in_a_project()
    {
        var save = Save();

        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => save.Check(new ContentFolder { ContentLink = new ContentReference(30) })).Code);
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => save.Check(new CmsCallTests.Unsecured(new ContentReference(31)))).Code);
        // New content has nothing to compare with.
        save.Check(Version(0, 0));
    }

    [Fact]
    public void A_dry_run_says_what_the_save_would_do()
    {
        _projects.AddItem(1, new ContentReference(10, 100), "en");
        var save = Save(ready: true);

        Assert.Equal((ProjectSaveItems.Added, true), Planned(save.Planned(Version(11, 110), review: false, intoVersion: null)));
        Assert.Equal((ProjectSaveItems.Replaced, true), Planned(save.Planned(Version(10, 100), review: false, intoVersion: null)));
        Assert.Equal((ProjectSaveItems.Unchanged, false), Planned(save.Planned(Version(10, 100), review: true, intoVersion: 100)));
        Assert.Equal((ProjectSaveItems.Added, true), Planned(save.Planned(Version(0, 0), review: false, intoVersion: null)));
        // A dry run changes nothing.
        Assert.DoesNotContain(_projects.Items, i => i.ContentLink.ID != 10);

        static (string, bool) Planned(ProjectSaveResult result) => (result.Item, result.Ready);
    }

    [Fact]
    public void A_save_without_project_warns_when_the_content_is_in_one()
    {
        _projects.Add(2, "Old", ProjectStatus.Published);
        _projects.AddItem(2, new ContentReference(11, 110), "en");
        _projects.AddItem(1, new ContentReference(10, 100), "en");

        var warning = ProjectSave.NotInProject(_call, Version(10, 100), intoVersion: null);
        Assert.Equal("warning", warning?.Severity);
        Assert.Contains("1 ('Launch', active), which holds version 10_100", warning!.Message);
        Assert.Contains("--project 1", warning.Message);

        // Saved into the version the project holds, published projects, other languages: nothing to say.
        Assert.Null(ProjectSave.NotInProject(_call, Version(10, 100), intoVersion: 100));
        Assert.Null(ProjectSave.NotInProject(_call, Version(11, 110), intoVersion: null));
        Assert.Null(ProjectSave.NotInProject(_call, Version(10, 100, language: "sv"), intoVersion: null));
        Assert.Null(ProjectSave.NotInProject(_call, Version(0, 0), intoVersion: null));
    }

    [Fact]
    public void The_warning_for_a_scheduled_project_says_to_reactivate_it_first()
    {
        _projects.Add(2, "Later", ProjectStatus.DelayedPublished);
        _projects.AddItem(2, new ContentReference(10, 100), "en");

        var warning = ProjectSave.NotInProject(_call, Version(10, 101), intoVersion: null);

        Assert.Contains("2 ('Later', delayedPublished), which holds version 10_100", warning!.Message);
        Assert.Contains("opticli projects reactivate 2", warning.Message);
    }

    [Fact]
    public void A_write_that_changed_nothing_puts_a_draft_in_but_not_a_published_version()
    {
        var save = Save();

        var (added, none) = save.AddUnchanged(Version(10, 100));
        Assert.Equal(ProjectSaveItems.Added, added.Item);
        Assert.Null(none);

        var (published, warning) = save.AddUnchanged(Version(11, 110, status: VersionStatus.Published));
        Assert.Equal(ProjectSaveItems.None, published.Item);
        Assert.Contains("isn't a draft", warning?.Message);
        Assert.Single(_projects.Items);
    }

    /// <summary>A language branch of a page, as the CMS loads it (read-only, so it can be cloned).</summary>
    private sealed class Page(ContentReference link, CultureInfo? language) : CmsCallTests.Secured(link, AccessLevel.FullAccess), ILocalizable, IVersionable, IReadOnly
    {
        public CultureInfo Language { get; set; } = language!;

        public IEnumerable<CultureInfo> ExistingLanguages { get; set; } = language is null ? [] : [language];

        public CultureInfo MasterLanguage { get; set; } = language!;

        public VersionStatus Status { get; set; } = VersionStatus.CheckedOut;

        public bool IsPendingPublish { get; set; }

        public DateTime? StartPublish { get; set; }

        public DateTime? StopPublish { get; set; }
#if CMS13

        public string? Variation { get; set; }
#endif

        public bool IsReadOnly => false;

        public void MakeReadOnly()
        {
        }

        public object CreateWritableClone() => MemberwiseClone();
    }
}
