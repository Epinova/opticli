using System.Text.Json;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Seals;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>
/// CMS projects on the CLI side: which writes take <c>project</c>/<c>ready</c>, plans' <c>project</c> (the plan's and the
/// steps'), naming a project, the projects a plan makes and marks ready, the publish prompt's summary and error hints.
/// </summary>
public class ProjectWritesTests
{
    private static ProjectInfo Project(int id, string name, string status = "active", int items = 0, int ready = 0) =>
        new(id, name, status, new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc), "opticli", null, items, ready);

    [Fact]
    public void Only_saves_take_a_project_and_never_with_a_publish()
    {
        Assert.Null(new SetOperation("123") { Project = "Spring" }.ProjectProblem());
        Assert.Null(new SetOperation("123") { Project = "12", Ready = true, RequestApproval = true }.ProjectProblem());
        Assert.Null(new UploadOperation("a.pdf", Parent: "5") { Project = "Spring" }.ProjectProblem());
        Assert.Null(new SetOperation("123").ProjectProblem());

        Assert.Contains("isn't published or scheduled by itself", new SetOperation("123", Publish: true) { Project = "Spring" }.ProjectProblem());
        Assert.Contains("isn't published or scheduled by itself", new CreateOperation("5", "StandardPage", "x") { Project = "Spring", PublishAt = DateTimeOffset.UtcNow.AddDays(1) }.ProjectProblem());
        Assert.Contains("give the project too", new SetOperation("123") { Ready = true }.ProjectProblem());
        Assert.Contains("takes no project", new PublishOperation("123") { Project = "Spring" }.ProjectProblem());
        Assert.Contains("takes no project", new TranslateOperation("123", "sv") { Remove = true, Project = "Spring" }.ProjectProblem());
        Assert.Contains("empty", new SetOperation("123") { Project = " " }.ProjectProblem());
    }

    [Fact]
    public void A_plans_project_goes_on_every_save_unless_a_step_names_another_or_none()
    {
        var plan = WritePlan.Parse("""
            {"project": "Spring campaign", "operations": [
              {"op": "create", "id": "page", "parent": 5, "type": "StandardPage", "name": "News", "ready": true},
              {"op": "set", "ref": "$page", "properties": {"Heading": "Hi"}, "project": 12},
              {"op": "area", "ref": "123", "property": "MainArea", "action": "add", "item": "456", "project": null},
              {"op": "move", "ref": "123", "to": "5"},
              {"op": "translate", "ref": "123", "lang": "sv", "remove": true, "confirm": true}
            ]}
            """);

        Assert.Equal("Spring campaign", plan.Project);
        var ops = plan.Steps.Select(s => s.Operation).ToList();
        Assert.Equal(("Spring campaign", true, true), (ops[0].Project, ops[0].ProjectFromPlan, ops[0].Ready));
        Assert.Equal(("12", false), (ops[1].Project, ops[1].ProjectFromPlan));
        Assert.Null(ops[2].Project);
        Assert.Null(ops[3].Project);
        Assert.Null(ops[4].Project);
    }

    [Theory]
    [InlineData("""{"project": "Spring", "operations": [{"op": "set", "ref": "1", "name": "x", "publish": true}]}""", "give it \"project\": null")]
    [InlineData("""{"operations": [{"op": "set", "ref": "1", "name": "x", "project": "Spring", "publishAt": "2030-01-01T08:00Z"}]}""", "isn't published or scheduled by itself")]
    [InlineData("""{"operations": [{"op": "set", "ref": "1", "name": "x", "ready": true}]}""", "give the project too")]
    [InlineData("""{"operations": [{"op": "publish", "ref": "1", "project": "Spring"}]}""", "unknown field \"project\"")]
    [InlineData("""{"project": "", "operations": [{"op": "set", "ref": "1", "name": "x"}]}""", "\"project\" must be a project's name")]
    [InlineData("""{"operations": [{"op": "set", "ref": "1", "name": "x", "project": true}]}""", "or null for none")]
    public void Projects_that_dont_fit_are_plan_problems(string json, string problem)
    {
        var error = Assert.Throws<UsageException>(() => WritePlan.Parse(json));

        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_is_named_by_id_or_exact_name_and_an_unpublished_one_wins()
    {
        IReadOnlyList<ProjectInfo> projects = [Project(3, "Spring", "published"), Project(7, "Spring"), Project(8, "Autumn", "published"), Project(9, "12")];

        Assert.Equal(7, ProjectReader.Find(projects, " spring ")?.Id);
        Assert.Equal(8, ProjectReader.Find(projects, "Autumn")?.Id);
        Assert.Equal(3, ProjectReader.Find(projects, "3")?.Id);
        Assert.Equal(9, ProjectReader.Find(projects, "12")?.Id);
        Assert.Null(ProjectReader.Find(projects, "Winter"));
        Assert.Contains("Did you mean 'Spring'", Assert.Throws<NotFoundException>(() => ProjectReader.Require(projects, "Sprng")).Hint);

        IReadOnlyList<ProjectInfo> twice = [Project(7, "Spring"), Project(10, "spring", "delayedPublished")];
        Assert.Contains("7 ('Spring', active), 10 ('spring', delayedPublished)", Assert.Throws<UsageException>(() => ProjectReader.Find(twice, "Spring")).Message);
    }

    [Fact]
    public void A_plan_makes_a_project_only_for_a_name_no_unpublished_project_has()
    {
        IReadOnlyList<ProjectInfo> projects = [Project(3, "Spring", "published"), Project(7, "Summer"), Project(8, "Old", "published")];

        Assert.Equal(7, PlanProjects.IdFor(projects, "summer"));
        Assert.Null(PlanProjects.IdFor(projects, "Spring"));
        Assert.Null(PlanProjects.IdFor(projects, "Winter"));
        // A published project named by its id is the site's to refuse; a number that is no id is never made into a name.
        Assert.Equal(8, PlanProjects.IdFor(projects, "8"));
        Assert.Contains("no project has that id", Assert.Throws<NotFoundException>(() => PlanProjects.IdFor(projects, "42")).Message);
    }

    [Fact]
    public void A_ready_step_whose_content_a_later_step_changes_is_made_ready_at_the_end()
    {
        var plan = WritePlan.Parse("""
            {"project": "Spring", "operations": [
              {"op": "create", "id": "page", "parent": 5, "type": "StandardPage", "name": "News", "ready": true},
              {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "456"},
              {"op": "set", "ref": "123", "name": "x", "ready": true},
              {"op": "set", "ref": "124", "name": "x", "ready": true},
              {"op": "set", "ref": "124", "name": "y", "project": null}
            ]}
            """);

        var steps = PlanProjects.DeferReady(plan.Steps.ToList());

        Assert.Equal([false, false, true, true, false], steps.Select(s => s.Operation.Ready));
    }

    [Fact]
    public void A_deferred_ready_takes_its_review_request_along_and_a_stopped_run_names_it()
    {
        var plan = WritePlan.Parse("""
            {"project": "Spring", "operations": [
              {"op": "set", "ref": "123", "name": "x", "ready": true, "requestApproval": true},
              {"op": "set", "ref": "124", "name": "x", "ready": true, "requestApproval": true},
              {"op": "set", "ref": "123", "name": "y"}
            ]}
            """);

        var steps = PlanProjects.DeferReady(plan.Steps.ToList());

        // Saved now, the review would start before the later change (or, without a sequence, be refused).
        Assert.Equal([false, true, false], steps.Select(s => s.Operation.RequestApproval));
        Assert.Contains("Operation(s) 0 asked for ready", PlanProjects.NotMarked(steps, plan.Steps, failed: 2));
        Assert.Null(PlanProjects.NotMarked(steps, plan.Steps, failed: 0));
    }

    [Fact]
    public void At_the_end_the_last_version_of_content_asked_ready_is_marked_unless_it_is()
    {
        var plan = WritePlan.Parse("""
            {"project": "Spring", "operations": [
              {"op": "create", "id": "page", "parent": 5, "type": "StandardPage", "name": "News", "ready": true},
              {"op": "set", "ref": "$page", "name": "x"},
              {"op": "set", "ref": "123", "name": "x", "ready": true, "requestApproval": true},
              {"op": "set", "ref": "124", "name": "x", "ready": true},
              {"op": "set", "ref": "125", "name": "x"}
            ]}
            """);
        PlanStepResult Saved(int index, string id, int version, string status, bool ready, bool review = false) =>
            new(index, "set", null, PlanStepStatus.Saved, new WriteOutput(id, $"{id}_{version}", null, "StandardPage", "x", "en", status, "5", true, false, false, true, null, [], null)
            {
                Project = new ProjectSaveResult(12, "Spring") { Item = ProjectSaveItems.Added, Ready = ready },
                ApprovalRequested = review ? true : null,
            });

        var marks = PlanProjects.Unready(plan.Steps, [
            Saved(0, "200", 1, "checkedOut", ready: false),
            Saved(1, "200", 1, "checkedOut", ready: false),
            Saved(2, "123", 2, "checkedOut", ready: false),
            Saved(3, "124", 3, "checkedIn", ready: true),
            Saved(4, "125", 4, "checkedOut", ready: false),
        ]).ToList();

        Assert.Equal([(12, false, (IReadOnlyList<string>)["200_1"]), (12, true, ["123_2"])], marks.Select(m => (m.Project, m.Review, m.Versions)).ToList(), new MarkComparer());
        Assert.Empty(PlanProjects.Unready(plan.Steps, [Saved(2, "123", 2, "awaitingApproval", ready: false, review: true)]));
    }

    private sealed class MarkComparer : IEqualityComparer<(int, bool, IReadOnlyList<string>)>
    {
        public bool Equals((int, bool, IReadOnlyList<string>) x, (int, bool, IReadOnlyList<string>) y) => x.Item1 == y.Item1 && x.Item2 == y.Item2 && x.Item3.SequenceEqual(y.Item3);

        public int GetHashCode((int, bool, IReadOnlyList<string>) obj) => HashCode.Combine(obj.Item1, obj.Item2);
    }

    [Fact]
    public void A_sealed_plan_refuses_projects()
    {
        var plan = WritePlan.Parse("""
            {"project": "Spring", "guidNamespace": "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02", "operations": [
              {"op": "set", "ref": "61104228-962d-4dd2-925c-b5b12e2373ee", "name": "x"},
              {"op": "set", "ref": "61104228-962d-4dd2-925c-b5b12e2373ee", "name": "y", "project": "Autumn"}
            ]}
            """);

        var problems = SealedPlan.Problems(plan, model: null);

        Assert.Equal(2, problems.Count(p => p.Contains("puts drafts in a project", StringComparison.Ordinal)));
        Assert.Contains(problems, p => p.StartsWith("The plan's \"project\"", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("operations[1] (set) has \"project\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_publish_prompt_says_what_goes_live_and_what_stays_behind()
    {
        static ContentSummary Content(int id, int version, string name, string status) =>
            new() { Ref = $"{id}_{version}", Id = id, Version = version, Guid = Guid.Empty, Name = name, Type = "StandardPage", Language = "en", Status = status };
        var preview = new ProjectResult
        {
            Project = new ProjectSummary { Id = 12, Name = "Spring", Status = "active", Items = 3, Ready = 1 },
            DryRun = true,
            Published = [new ProjectItemResult
            {
                Content = Content(123, 456, "News", "checkedIn"),
                Outcome = ProjectItemOutcomes.Published,
                PendingDraft = new PendingDraft("123_455", "bob", new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc), [new PropertyChange("Heading", null, JsonDocument.Parse("\"Hi\"").RootElement)]),
            }],
            NotReady = [new ProjectItemResult { Content = Content(124, 460, "About", "checkedOut"), Outcome = ProjectItemOutcomes.NotReady }],
            AlreadyPublished = [new ProjectItemResult { Content = Content(125, 470, "Home", "published"), Outcome = ProjectItemOutcomes.AlreadyPublished }],
        };

        var summary = ProjectWrites.Summary(preview);

        Assert.StartsWith("Project 12 ('Spring', active): publishing puts 1 item live, the ones marked Ready to publish:", summary, StringComparison.Ordinal);
        Assert.Contains("  123_456 'News' (StandardPage), en, checkedIn", summary, StringComparison.Ordinal);
        Assert.Contains("also puts live changes saved by bob in 123_455", summary, StringComparison.Ordinal);
        Assert.Contains("Stays behind, not ready (1):\n  124_460 'About'", summary.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Published already, left as it is (1):", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_refusals_get_hints_in_command_line_terms_and_keep_every_item()
    {
        var project = new ProjectResult { Project = new ProjectSummary { Id = 12, Name = "Spring", Status = "active" }, Valid = false };
        OptiCliException Refusal(string code, string reason) =>
            AgentErrors.ToException(new AgentError(code, "Can't publish project 12.", "Retry with includeDraft.") { Reason = reason, Project = project });

        var pending = ProjectWrites.Translate(Refusal(AgentErrorCodes.Conflict, AgentErrorReasons.PendingDraft), AgentRoutes.ProjectPublish("12"));
        Assert.IsType<ConflictException>(pending);
        Assert.Contains("--include-draft", pending!.Hint);
        Assert.Same(project, Assert.IsType<AgentErrorDetails>(pending.Details).Project);

        var review = ProjectWrites.Translate(Refusal(AgentErrorCodes.Refused, AgentErrorReasons.ApprovalSequence), AgentRoutes.ProjectReady("Spring"));
        Assert.IsType<RefusedException>(review);
        Assert.Contains("--request-approval", review!.Hint);

        var mixed = ProjectWrites.Translate(Refusal(AgentErrorCodes.Validation, AgentErrorReasons.ProjectItems), AgentRoutes.ProjectPublish("12"));
        Assert.Equal(ErrorCode.Validation, mixed!.Code);
        Assert.Contains("details.project", mixed.Hint);

        // The publish's own approval hint (save the items again for review) stays as the site words it.
        Assert.Null(ProjectWrites.Translate(Refusal(AgentErrorCodes.Refused, AgentErrorReasons.ApprovalSequence), AgentRoutes.ProjectPublish("12")));
    }
}
