using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Cms;
using OptiCli.Cms;
using OptiCli.Cms.Operations;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Projects;

/// <summary>
/// Reading projects through the CMS's repository, as the MCP module's <c>list_projects</c>, <c>get_project</c> and
/// <c>get_content</c> do: which projects are listed, and the projects that hold a version of content.
/// </summary>
public class ProjectReadTests
{
    private readonly FakeProjects _projects = new();

    private readonly CmsCall _call;

    public ProjectReadTests()
    {
        var (repository, _) = Recorder<IContentRepository>.Create();
        var services = new ServiceCollection()
            .AddSingleton<ProjectRepository>(_projects)
            .AddSingleton(repository)
            .BuildServiceProvider();
        _call = new CmsCall(services, CancellationToken.None, CmsCaller.Developer);
    }

    [Fact]
    public void The_list_is_newest_first_and_leaves_published_projects_out_unless_asked()
    {
        _projects.Add(1, "Launch", ProjectStatus.Published);
        _projects.Add(2, "Spring campaign");
        _projects.Add(3, "Autumn campaign", ProjectStatus.DelayedPublished);
        _projects.Add(4, "Retry", ProjectStatus.PublishFailed);
        _projects.AddItem(2, new ContentReference(5, 10), "en");
        _projects.AddItem(2, new ContentReference(6, 20), "sv");

        var open = ProjectReadOperation.List(_call, all: false, limit: null);
        Assert.Equal([4, 3, 2], open.Projects.Select(p => p.Id));
        Assert.Equal(["publishFailed", "delayedPublished", "active"], open.Projects.Select(p => p.Status));
        Assert.Equal(2, open.Projects.Single(p => p.Id == 2).Items);
        Assert.False(open.More);

        var all = ProjectReadOperation.List(_call, all: true, limit: 2);
        Assert.Equal([4, 3], all.Projects.Select(p => p.Id));
        Assert.True(all.More);
        Assert.Equal([4, 3, 2, 1], ProjectReadOperation.List(_call, all: true, limit: null).Projects.Select(p => p.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void A_limit_outside_its_range_is_a_usage_error(int limit) =>
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => ProjectReadOperation.List(_call, all: false, limit)).Code);

    [Fact]
    public void Content_shows_every_project_that_holds_a_version_of_it()
    {
        _projects.Add(1, "Launch", ProjectStatus.Published);
        _projects.Add(2, "Spring campaign");
        _projects.AddItem(2, new ContentReference(5, 12), "sv");
        _projects.AddItem(1, new ContentReference(5, 10), "en");
        _projects.AddItem(2, new ContentReference(6, 20), "en");

        var held = ProjectReadOperation.Of(_call, new ContentReference(5, 11));

        Assert.Equal(
            [new ContentProject(1, "Launch", "published", "5_10", "en"), new ContentProject(2, "Spring campaign", "active", "5_12", "sv")],
            held);
        Assert.Null(ProjectReadOperation.Of(_call, new ContentReference(7)));
    }

    [Fact]
    public void A_failure_to_read_projects_never_fails_the_read_of_content()
    {
        _projects.ItemsFailure = new InvalidOperationException("The project tables are gone.");

        Assert.Null(ProjectReadOperation.Of(_call, new ContentReference(5)));
    }

    [Fact]
    public void Every_item_state_has_its_name()
    {
        Assert.Equal(
            [ProjectItemStates.Draft, ProjectItemStates.Ready, ProjectItemStates.InReview, ProjectItemStates.Scheduled, ProjectItemStates.Published, ProjectItemStates.Deleted, ProjectItemStates.Missing],
            Enum.GetValues<ProjectItemState>().Select(ProjectReadOperation.StateName));
    }
}
