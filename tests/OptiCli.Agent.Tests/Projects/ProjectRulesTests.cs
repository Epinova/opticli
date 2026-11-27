using EPiServer.Core;
using EPiServer.DataAbstraction;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Projects;

/// <summary>
/// What each project status allows (as the edit UI: only active and publishFailed projects take items or publish), where
/// an item stands, and how a refusal over several items is made.
/// </summary>
public class ProjectRulesTests
{
    internal static CmsCall Developer() => new(new ServiceCollection().BuildServiceProvider(), CancellationToken.None, CmsCaller.Developer);

    [Theory]
    [InlineData(ProjectStatus.Active, true)]
    [InlineData(ProjectStatus.PublishFailed, true)]
    [InlineData(ProjectStatus.Publishing, false)]
    [InlineData(ProjectStatus.Published, false)]
    [InlineData(ProjectStatus.DelayedPublished, false)]
    [InlineData(ProjectStatus.Reactivating, false)]
    public void Only_active_and_failed_projects_take_changes(ProjectStatus status, bool active)
    {
        var project = new Project { ID = 3, Name = "Launch", Status = status };

        if (active)
        {
            ProjectRules.RequireActive(Developer(), project, "take items");
            return;
        }
        var error = Assert.Throws<AgentException>(() => ProjectRules.RequireActive(Developer(), project, "take items"));
        Assert.Equal((AgentErrorCodes.Conflict, AgentErrorReasons.ProjectNotActive), (error.Code, error.Reason));
        Assert.Contains("3 ('Launch'", error.Message);
    }

    [Fact]
    public void A_scheduled_project_says_when_and_points_to_reactivate()
    {
        var project = new Project { ID = 3, Name = "Launch", Status = ProjectStatus.DelayedPublished, DelayPublishUntil = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc) };

        var error = Assert.Throws<AgentException>(() => ProjectRules.RequireActive(Developer(), project, "take items"));

        Assert.Contains("2030-01-02 03:04:05Z", error.Message);
        Assert.Contains("opticli projects reactivate 3", error.Hint);
    }

    [Fact]
    public void Only_a_scheduled_project_is_reactivated()
    {
        ProjectRules.RequireScheduled(new Project { ID = 1, Name = "a", Status = ProjectStatus.DelayedPublished });

        var error = Assert.Throws<AgentException>(() => ProjectRules.RequireScheduled(new Project { ID = 1, Name = "a", Status = ProjectStatus.Active }));
        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
    }

    [Theory]
    [InlineData(ProjectStatus.Active, true)]
    [InlineData(ProjectStatus.PublishFailed, true)]
    [InlineData(ProjectStatus.Published, true)]
    [InlineData(ProjectStatus.DelayedPublished, false)]
    [InlineData(ProjectStatus.Publishing, false)]
    [InlineData(ProjectStatus.Reactivating, false)]
    public void A_project_is_deleted_unless_it_is_scheduled_or_busy(ProjectStatus status, bool deletable)
    {
        var project = new Project { ID = 1, Name = "a", Status = status };

        if (deletable)
        {
            ProjectRules.RequireDeletable(Developer(), project);
        }
        else
        {
            Assert.Equal(AgentErrorCodes.Conflict, Assert.Throws<AgentException>(() => ProjectRules.RequireDeletable(Developer(), project)).Code);
        }
    }

    [Theory]
    [InlineData(VersionStatus.CheckedOut, false, "Draft")]
    [InlineData(VersionStatus.Rejected, false, "Draft")]
    [InlineData(VersionStatus.CheckedIn, false, "Ready")]
    [InlineData(VersionStatus.AwaitingApproval, false, "InReview")]
    [InlineData(VersionStatus.DelayedPublish, false, "Scheduled")]
    [InlineData(VersionStatus.Published, false, "Published")]
    [InlineData(VersionStatus.PreviouslyPublished, false, "Published")]
    [InlineData(VersionStatus.CheckedIn, true, "Deleted")]
    [InlineData(null, false, "Missing")]
    public void An_item_stands_where_its_version_does(VersionStatus? status, bool deleted, string state) =>
        Assert.Equal(Enum.Parse<ProjectItemState>(state), ProjectRules.State(status, deleted));

    [Theory]
    [InlineData("Ready", false, ProjectItemOutcomes.Published)]
    [InlineData("Ready", true, ProjectItemOutcomes.Scheduled)]
    [InlineData("Published", false, ProjectItemOutcomes.AlreadyPublished)]
    [InlineData("Draft", false, ProjectItemOutcomes.NotReady)]
    [InlineData("InReview", false, ProjectItemOutcomes.NotReady)]
    [InlineData("Scheduled", true, ProjectItemOutcomes.NotReady)]
    [InlineData("Deleted", false, ProjectItemOutcomes.NotReady)]
    public void A_publish_takes_only_the_ready_items(string state, bool scheduling, string outcome) =>
        Assert.Equal(outcome, ProjectRules.PublishOutcome(Enum.Parse<ProjectItemState>(state), scheduling));

    [Theory]
    [InlineData("Draft", null, null)]
    [InlineData("Ready", null, null)]
    [InlineData("InReview", AgentErrorCodes.Conflict, AgentErrorReasons.InReview)]
    [InlineData("Published", AgentErrorCodes.Conflict, null)]
    [InlineData("Scheduled", AgentErrorCodes.Conflict, null)]
    [InlineData("Deleted", AgentErrorCodes.Conflict, null)]
    [InlineData("Missing", AgentErrorCodes.NotFound, null)]
    public void Only_drafts_are_marked_ready(string state, string? code, string? reason)
    {
        var problem = ProjectRules.CantMarkReady(Enum.Parse<ProjectItemState>(state), "123 ('News') in 'en'");

        Assert.Equal(code, problem?.Code);
        Assert.Equal(reason, problem?.Reason);
        Assert.True(problem is null || problem.Message.StartsWith("123 ('News') in 'en'", StringComparison.Ordinal));
    }

    private static ProjectItemResult Item(int id, ProjectItemProblem? problem = null, PendingDraft? pending = null) => new()
    {
        Content = new ContentSummary { Ref = $"{id}_1", Id = id, Guid = Guid.Empty, Name = $"Page {id}" },
        Outcome = ProjectItemOutcomes.Published,
        Problem = problem,
        PendingDraft = pending,
    };

    private static readonly ProjectResult Result = new() { Project = new ProjectSummary { Id = 3, Name = "Launch", Status = ProjectStatuses.Active } };

    private static PendingDraft Pending(string version) => new(version, "colleague@example.com", DateTime.UtcNow, []);

    [Fact]
    public void A_refusal_over_newer_published_versions_is_confirmed_by_includeDraft_also_mixed_with_others_drafts()
    {
        var replaced = new ProjectItemProblem(AgentErrorCodes.Conflict, "1: would replace the published version 1_9", AgentErrorReasons.NewerPublished);

        var only = ProjectRules.Refusal("publish", "3 ('Launch')", Result, [Item(1, replaced), Item(2)]);
        Assert.Equal((AgentErrorCodes.Conflict, AgentErrorReasons.NewerPublished), (only.Code, only.Reason));
        Assert.Contains("retry with includeDraft", only.Hint);

        var mixed = ProjectRules.Refusal("publish", "3 ('Launch')", Result,
            [Item(1, replaced), Item(3, new ProjectItemProblem(AgentErrorCodes.Conflict, "3: a draft", AgentErrorReasons.PendingDraft), Pending("3_9"))]);
        Assert.Equal(AgentErrorReasons.ProjectItems, mixed.Reason);
        Assert.Contains("retry with includeDraft", mixed.Hint);
        Assert.Contains("newerPublished", mixed.Hint);
    }

    [Fact]
    public void A_refusal_has_the_code_of_its_most_serious_problem_and_carries_every_item()
    {
        List<ProjectItemResult> items =
        [
            Item(1),
            Item(2, new ProjectItemProblem(AgentErrorCodes.Conflict, "2: someone's draft", AgentErrorReasons.PendingDraft), Pending("2_5")),
            Item(3, new ProjectItemProblem(AgentErrorCodes.Validation, "3 doesn't pass validation.")),
            Item(4, new ProjectItemProblem(AgentErrorCodes.Refused, "4 has an approval sequence", AgentErrorReasons.ApprovalSequence)),
        ];

        var error = ProjectRules.Refusal("publish", "3 ('Launch', active)", Result, items);

        Assert.Equal((AgentErrorCodes.Refused, AgentErrorReasons.ProjectItems), (error.Code, error.Reason));
        Assert.Contains("3 of 4 item(s)", error.Message);
        Assert.Null(error.PendingDraft);
        Assert.False(error.Project!.Valid);
        Assert.Same(Result.Project, error.Project.Project);
        Assert.Equal(error.Reason, error.ToError().Reason);
        Assert.NotNull(error.ToError().Project);
    }

    [Fact]
    public void Other_peoples_drafts_in_several_items_are_the_pending_draft_conflict_that_include_draft_confirms()
    {
        List<ProjectItemResult> items =
        [
            Item(1, new ProjectItemProblem(AgentErrorCodes.Conflict, "1: a draft", AgentErrorReasons.PendingDraft), Pending("1_4")),
            Item(2),
            Item(3, new ProjectItemProblem(AgentErrorCodes.Conflict, "3: a draft", AgentErrorReasons.PendingDraft), Pending("3_9")),
        ];

        var error = ProjectRules.Refusal("publish", "3 ('Launch', active)", Result, items);

        Assert.Equal((AgentErrorCodes.Conflict, AgentErrorReasons.PendingDraft), (error.Code, error.Reason));
        Assert.Equal("1_4", error.PendingDraft?.Version);
        Assert.Contains("includeDraft", error.Hint);
        Assert.Contains("2 of 3 item(s)", error.Message);
    }

    [Fact]
    public void Approval_sequences_alone_point_to_request_approval()
    {
        var error = ProjectRules.Refusal("mark items ready in", "3", Result,
            [Item(1, new ProjectItemProblem(AgentErrorCodes.Refused, "1 has an approval sequence", AgentErrorReasons.ApprovalSequence))]);

        Assert.Equal((AgentErrorCodes.Refused, AgentErrorReasons.ApprovalSequence), (error.Code, error.Reason));
        Assert.Contains("requestApproval", error.Hint);
        Assert.NotNull(error.Project);

        // A publish takes no requestApproval: it gives its own hint.
        var publish = ProjectRules.Refusal("publish", "3", Result,
            [Item(1, new ProjectItemProblem(AgentErrorCodes.Refused, "1 has an approval sequence", AgentErrorReasons.ApprovalSequence))], "Send them for review.");
        Assert.Equal("Send them for review.", publish.Hint);
    }

    [Fact]
    public void An_item_checks_refusal_becomes_its_problem_named_once()
    {
        var problem = ProjectRules.Problem(new AgentException(AgentErrorCodes.Conflict, "Publishing 5 ('Home') would also put live x.") { PendingDraft = Pending("5_2") }, "5 ('Home')");
        Assert.Equal((AgentErrorCodes.Conflict, AgentErrorReasons.PendingDraft, "5 ('Home'): Publishing 5 ('Home') would also put live x."), (problem.Code, problem.Reason, problem.Message));

        var named = ProjectRules.Problem(AgentException.Refused("5 ('Home') is locked."), "5 ('Home')");
        Assert.Equal("5 ('Home') is locked.", named.Message);
    }

    [Theory]
    [InlineData(ProjectStatus.Active, "active")]
    [InlineData(ProjectStatus.PublishFailed, "publishFailed")]
    [InlineData(ProjectStatus.DelayedPublished, "delayedPublished")]
    public void Statuses_are_named_as_the_database_read_names_them(ProjectStatus status, string name) =>
        Assert.Equal(name, ProjectRules.StatusName(status));

    [Theory]
    [InlineData(null, true, ProjectEditorUi.ProjectMode)]
    [InlineData(true, false, ProjectEditorUi.ProjectMode)]
    [InlineData(false, true, ProjectEditorUi.Gadget)]
    [InlineData(false, false, CmsProjectUi.None)]
    public void Editors_lack_projects_only_with_project_mode_and_the_gadget_both_off(bool? projectMode, bool gadget, string ui) =>
        Assert.Equal(ui, CmsProjectUi.Decide(projectMode, gadget));

    [Fact]
    public void A_site_without_the_edit_ui_has_projects()
    {
        // No ProjectUIOptions here (the tests don't load EPiServer.Cms.Shell.UI): nothing to refuse.
        Assert.Null(CmsProjectUi.Of(new ServiceCollection().BuildServiceProvider()));
    }
}
