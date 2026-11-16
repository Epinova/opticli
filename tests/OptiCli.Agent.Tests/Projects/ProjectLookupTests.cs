using EPiServer.DataAbstraction;
using OptiCli.Cms;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Projects;

/// <summary>Which project a request names: by id, or by its exact name, which the CMS doesn't keep unique.</summary>
public class ProjectLookupTests
{
    private static Project Find(FakeProjects projects, string? reference) => ProjectLookup.Find(projects.List(), projects.Get, reference);

    [Fact]
    public void An_id_names_that_project()
    {
        var projects = new FakeProjects();
        projects.Add(7, "Spring campaign");
        projects.Add(12, "Autumn");

        Assert.Equal(12, Find(projects, "12").ID);
        Assert.Equal(12, Find(projects, " 12 ").ID);
    }

    [Fact]
    public void A_name_is_matched_whole_ignoring_case_and_surrounding_spaces()
    {
        var projects = new FakeProjects();
        projects.Add(7, "Spring campaign");
        projects.Add(8, "Spring campaign 2027");

        Assert.Equal(7, Find(projects, "spring CAMPAIGN ").ID);
        var missing = Assert.Throws<AgentException>(() => Find(projects, "Spring"));
        Assert.Equal(AgentErrorCodes.NotFound, missing.Code);
    }

    [Fact]
    public void A_number_no_project_has_as_id_is_tried_as_a_name()
    {
        var projects = new FakeProjects();
        projects.Add(3, "2027");

        Assert.Equal(3, Find(projects, "2027").ID);
    }

    [Fact]
    public void The_unpublished_project_wins_over_published_ones_of_the_same_name()
    {
        var projects = new FakeProjects();
        projects.Add(4, "Newsletter", ProjectStatus.Published);
        projects.Add(9, "Newsletter", ProjectStatus.Published);
        projects.Add(15, "Newsletter");

        Assert.Equal(15, Find(projects, "Newsletter").ID);
    }

    [Fact]
    public void A_name_only_one_published_project_has_names_it()
    {
        var projects = new FakeProjects();
        projects.Add(4, "Newsletter", ProjectStatus.Published);

        Assert.Equal(4, Find(projects, "Newsletter").ID);
    }

    [Theory]
    [InlineData(ProjectStatus.Active, ProjectStatus.DelayedPublished)]
    [InlineData(ProjectStatus.Published, ProjectStatus.Published)]
    public void A_name_two_candidates_have_is_a_usage_error_listing_their_ids(ProjectStatus first, ProjectStatus second)
    {
        var projects = new FakeProjects();
        projects.Add(21, "Launch", second);
        projects.Add(20, "Launch", first);

        var error = Assert.Throws<AgentException>(() => Find(projects, "launch"));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.Contains("20 ('Launch'", error.Message);
        Assert.Contains("21 ('Launch'", error.Message);
        Assert.True(error.Message.IndexOf("20 (", StringComparison.Ordinal) < error.Message.IndexOf("21 (", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_name_suggests_close_ones()
    {
        var projects = new FakeProjects();
        projects.Add(7, "Spring campaign");

        var error = Assert.Throws<AgentException>(() => Find(projects, "Sprng campaign"));

        Assert.Equal(AgentErrorCodes.NotFound, error.Code);
        Assert.Contains("'Spring campaign'", error.Hint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_name_is_a_usage_error(string? reference) =>
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => Find(new FakeProjects(), reference)).Code);

    [Fact]
    public void A_name_is_taken_only_by_a_project_that_isnt_published()
    {
        var projects = new FakeProjects();
        projects.Add(4, "Newsletter", ProjectStatus.Published);
        projects.Add(5, "Launch", ProjectStatus.DelayedPublished);

        Assert.Null(ProjectLookup.Taken(projects.Projects, "newsletter"));
        Assert.Equal(5, ProjectLookup.Taken(projects.Projects, " LAUNCH")?.ID);
    }
}
