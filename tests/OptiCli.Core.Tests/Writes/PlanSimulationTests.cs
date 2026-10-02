using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class PlanSimulationTests
{
    private static readonly WritePlan Plan = WritePlan.Parse("""
        {"guidNamespace": "6f1c2b3a-9d4e-4f5a-8b7c-1d2e3f4a5b6c", "operations": [
          {"op": "create", "id": "root", "parent": "100", "type": "CategoryPage", "name": "Root"},
          {"op": "create", "id": "page", "parent": "$root", "type": "ArticlePage", "name": "Page", "properties": {"Heading": "One"}, "publish": true},
          {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page", "properties": {"Link": "$root", "Text": "t"}},
          {"op": "set", "ref": "$page", "properties": {"Author": "Kari", "MainArea": [{"ref": "$teaser"}]}},
          {"op": "set", "ref": "$page", "name": "Page 2", "properties": {"Heading": "Two"}, "publish": true},
          {"op": "publish", "ref": "$root"},
          {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"}
        ]}
        """);

    private static readonly Dictionary<string, int> None = [];

    [Fact]
    public void A_create_under_planned_content_runs_under_the_nearest_existing_ancestor_with_the_planned_parent_type()
    {
        var simulation = PlanSimulation.For(Plan.Steps[1], Plan.Steps, None, updateExisting: false)!;

        var create = Assert.IsType<CreateOperation>(simulation.Operation);
        Assert.Equal(("100", "CategoryPage", true), (create.Parent, create.PlannedParentType, create.Publish));
        Assert.Equal(Plan.Steps[1].Operation.ContentGuid, create.ContentGuid);
        Assert.Null(create.Id);
        Assert.Equal(new Dictionary<string, string> { ["$root"] = "100" }, simulation.StandIns);
    }

    [Fact]
    public void Values_that_refer_to_planned_content_are_left_out_and_reported()
    {
        var simulation = PlanSimulation.For(Plan.Steps[2], Plan.Steps, None, updateExisting: false)!;

        var block = Assert.IsType<BlockCreateOperation>(simulation.Operation);
        Assert.Equal("100", block.For);
        Assert.Equal("t", (string?)block.Properties!["Text"]);
        Assert.False(block.Properties.ContainsKey("Link"));
        Assert.Equal(["Link"], simulation.Unchecked);
        Assert.Equal([("Link", "root")], simulation.References);
    }

    [Fact]
    public void A_set_on_planned_content_is_the_content_as_it_will_be_after_that_step()
    {
        var afterFirstSet = Assert.IsType<CreateOperation>(PlanSimulation.For(Plan.Steps[3], Plan.Steps, None, updateExisting: false)!.Operation);
        Assert.Equal(("One", "Kari", false), ((string?)afterFirstSet.Properties!["Heading"], (string?)afterFirstSet.Properties["Author"], afterFirstSet.Publish));
        Assert.False(afterFirstSet.Properties.ContainsKey("MainArea"));
        Assert.Null(afterFirstSet.ContentGuid);

        var afterSecondSet = Assert.IsType<CreateOperation>(PlanSimulation.For(Plan.Steps[4], Plan.Steps, None, updateExisting: false)!.Operation);
        Assert.Equal(("Two", "Kari", "Page 2", true), ((string?)afterSecondSet.Properties!["Heading"], (string?)afterSecondSet.Properties["Author"], afterSecondSet.Name, afterSecondSet.Publish));
    }

    [Fact]
    public void A_publish_of_planned_content_is_a_publishing_create()
    {
        var publish = Assert.IsType<CreateOperation>(PlanSimulation.For(Plan.Steps[5], Plan.Steps, None, updateExisting: false)!.Operation);

        Assert.Equal(("100", true, "Root"), (publish.Parent, publish.Publish, publish.Name));
    }

    [Fact]
    public void Existing_content_is_used_as_is_and_not_simulated()
    {
        var existing = new Dictionary<string, int> { ["root"] = 500, ["teaser"] = 700 };

        var page = Assert.IsType<CreateOperation>(PlanSimulation.For(Plan.Steps[1], Plan.Steps, existing, updateExisting: true)!.Operation);
        Assert.Equal(("500", null), (page.Parent, page.PlannedParentType));
        Assert.Null(page.ContentGuid);
        Assert.Null(PlanSimulation.For(Plan.Steps[5], Plan.Steps, existing, updateExisting: true));

        var set = Assert.IsType<CreateOperation>(PlanSimulation.For(Plan.Steps[3], Plan.Steps, existing, updateExisting: true)!.Operation);
        Assert.Equal("700", (string?)set.Properties!["MainArea"]![0]!["ref"]);
    }

    [Fact]
    public void Area_edits_are_not_simulated() => Assert.Null(PlanSimulation.For(Plan.Steps[6], Plan.Steps, None, updateExisting: false));

    [Fact]
    public void Area_edits_on_planned_content_are_part_of_what_later_steps_simulate()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Page"},
              {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page"},
              {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "200", "display": "wide"},
              {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "300", "at": 0},
              {"op": "area", "ref": "$page", "property": "MainArea", "action": "remove", "item": "300"},
              {"op": "publish", "ref": "$page"},
              {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser"},
              {"op": "publish", "ref": "$page"}
            ]}
            """);

        var existingOnly = Assert.IsType<CreateOperation>(PlanSimulation.For(plan.Steps[5], plan.Steps, None, updateExisting: false)!.Operation);
        Assert.Equal("""[{"ref":"200","displayOption":"wide"}]""", existingOnly.Properties!["MainArea"]!.ToJsonString());

        var withPlanned = PlanSimulation.For(plan.Steps[7], plan.Steps, None, updateExisting: false)!;
        Assert.Null(withPlanned.Operation is CreateOperation { Properties: { } values } ? values["MainArea"] : null);
        Assert.Equal(["MainArea"], withPlanned.Unchecked);
        Assert.Equal([("MainArea", "teaser")], withPlanned.References);
    }

    [Fact]
    public void Steps_without_a_planned_target_are_not_simulated_as_an_earlier_step_without_an_id()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "block", "type": "TeaserBlock", "name": "Shared", "parent": "100"},
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Page"},
              {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "200"},
              {"op": "move", "ref": "300", "to": "100"}
            ]}
            """);

        Assert.Null(PlanSimulation.For(plan.Steps[2], plan.Steps, None, updateExisting: false));
        Assert.Null(PlanSimulation.For(plan.Steps[3], plan.Steps, None, updateExisting: false));
    }
}

public class PlanSimulationOnExistingTests
{
    private static readonly Dictionary<string, int> None = [];

    private static PlanTarget Target(int id = 123, string? language = "en", bool branchExists = true, bool versioned = false) =>
        new(id, language, "en", branchExists, versioned);

    private static Simulation? For(WritePlan plan, int index, IReadOnlyDictionary<int, PlanTarget> targets, bool updateExisting = false) =>
        PlanSimulation.OnExisting(plan.Steps[index], plan.Steps, targets, None, updateExisting);

    [Fact]
    public void A_publish_after_set_and_area_steps_is_their_changes_published()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "set", "ref": "123", "name": "News", "properties": {"Heading": "One", "Intro": "i"}},
              {"op": "area", "ref": "123", "property": "MainArea", "action": "add", "item": "456"},
              {"op": "set", "ref": "/en/news/", "properties": {"Heading": "Two"}},
              {"op": "publish", "ref": "123", "includeDraft": true}
            ]}
            """);
        var targets = Enumerable.Range(0, 4).ToDictionary(i => i, _ => Target());

        var simulation = For(plan, 3, targets)!;

        var set = Assert.IsType<SetOperation>(simulation.Operation);
        Assert.Equal(("123", "News", (string?)null, true, true), (set.Ref, set.Name, set.Lang, set.Publish, set.IncludeDraft));
        Assert.Equal("""{"Intro":"i","Heading":"Two"}""", set.Properties!.ToJsonString());
        Assert.Equal("456", Assert.Single(set.AreaEdits!).Item);
        Assert.Contains("after operation(s) 0, 1, 2, published", Assert.Single(simulation.Notes!));
    }

    [Fact]
    public void A_whole_area_set_later_replaces_earlier_area_edits()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "area", "ref": "123", "property": "MainArea", "action": "add", "item": "456"},
              {"op": "set", "ref": "123", "properties": {"mainArea": [{"ref": "789"}]}},
              {"op": "area", "ref": "123", "property": "MainArea", "action": "add", "item": "999"},
              {"op": "publish", "ref": "123"}
            ]}
            """);
        var targets = Enumerable.Range(0, 4).ToDictionary(i => i, _ => Target());

        var set = Assert.IsType<SetOperation>(For(plan, 3, targets)!.Operation);

        Assert.Equal("999", Assert.Single(set.AreaEdits!).Item);
        Assert.Equal("""[{"ref":"789"}]""", set.Properties!["mainArea"]!.ToJsonString());
    }

    [Fact]
    public void Steps_on_other_content_branches_or_versions_are_not_folded_in()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "set", "ref": "123", "properties": {"Heading": "One"}},
              {"op": "publish", "ref": "123", "lang": "sv"},
              {"op": "publish", "ref": "124"},
              {"op": "publish", "ref": "123", "version": 77},
              {"op": "set", "ref": "123", "properties": {"Heading": "Two"}}
            ]}
            """);
        var targets = new Dictionary<int, PlanTarget>
        {
            [0] = Target(), [1] = Target(language: "sv"), [2] = Target(id: 124), [3] = Target(versioned: true), [4] = Target(),
        };

        Assert.Null(For(plan, 0, targets));
        Assert.Null(For(plan, 1, targets));
        Assert.Null(For(plan, 2, targets));
        Assert.Null(For(plan, 3, targets));
        // A later set is dry-run against the database as it is: its draft is based on the latest version anyway.
        Assert.Null(For(plan, 4, targets));
    }

    [Fact]
    public void A_publish_right_after_a_step_that_publishes_has_nothing_to_publish()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "set", "ref": "123", "properties": {"Heading": "One"}, "publish": true},
              {"op": "publish", "ref": "123"}
            ]}
            """);
        var targets = new Dictionary<int, PlanTarget> { [0] = Target(), [1] = Target() };

        var conflict = Assert.Throws<OptiCli.Core.Errors.ConflictException>(() => For(plan, 1, targets));
        Assert.Contains("Operation 0 already publishes 123 in 'en'", conflict.Message);
        // Run again with --update-existing, such a publish changes nothing.
        Assert.Null(For(plan, 1, targets, updateExisting: true));
    }

    [Fact]
    public void Values_that_refer_to_planned_content_are_left_out()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "parent": "100"},
              {"op": "set", "ref": "123", "properties": {"Teaser": "$teaser", "Heading": "One"}},
              {"op": "area", "ref": "123", "property": "MainArea", "action": "add", "item": "$teaser"},
              {"op": "publish", "ref": "123"}
            ]}
            """);
        var targets = new Dictionary<int, PlanTarget> { [1] = Target(), [2] = Target(), [3] = Target() };

        var simulation = For(plan, 3, targets)!;

        var set = Assert.IsType<SetOperation>(simulation.Operation);
        Assert.Equal("""{"Heading":"One"}""", set.Properties!.ToJsonString());
        Assert.Empty(set.AreaEdits!);
        Assert.Equal(["MainArea", "Teaser"], simulation.Unchecked.Order());
    }

    [Fact]
    public void Steps_on_a_branch_the_plan_translates_are_dry_run_as_the_new_branch()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "translate", "ref": "123", "lang": "sv", "name": "Nyheter", "properties": {"Heading": "Hej"}},
              {"op": "set", "ref": "123", "lang": "sv", "properties": {"Intro": "i"}},
              {"op": "area", "ref": "123", "lang": "sv", "property": "MainArea", "action": "add", "item": "456", "publish": true},
              {"op": "set", "ref": "123", "lang": "sv", "properties": {"Heading": "Hallå"}, "publish": true},
              {"op": "publish", "ref": "123", "lang": "sv"}
            ]}
            """);
        var targets = Enumerable.Range(0, 5).ToDictionary(i => i, _ => Target(language: "sv", branchExists: false));

        var set = Assert.IsType<TranslateOperation>(For(plan, 1, targets)!.Operation);
        Assert.Equal(("123", "sv", "Nyheter", false), (set.Ref, set.Lang, set.Name, set.Publish));
        Assert.Equal("""{"Heading":"Hej","Intro":"i"}""", set.Properties!.ToJsonString());

        var area = For(plan, 2, targets)!;
        var onMaster = Assert.IsType<AreaEdit>(area.Operation);
        Assert.Equal(("en", false, true), (onMaster.Lang, onMaster.Publish, onMaster.Force));
        Assert.Contains("master branch ('en')", Assert.Single(area.Notes!));

        var publishingSet = For(plan, 3, targets)!;
        var values = Assert.IsType<TranslateOperation>(publishingSet.Operation).Properties!;
        Assert.Equal(("Hallå", "i"), ((string?)values["Heading"], (string?)values["Intro"]));
        Assert.Contains("MainArea", publishingSet.Unchecked);

        // The set before it already publishes the branch.
        Assert.Throws<OptiCli.Core.Errors.ConflictException>(() => For(plan, 4, targets));
    }

    [Fact]
    public void A_publish_after_a_translate_publishes_the_new_branch()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "translate", "ref": "123", "lang": "sv", "properties": {"Heading": "Hej"}},
              {"op": "publish", "ref": "123", "lang": "sv"}
            ]}
            """);
        var targets = new Dictionary<int, PlanTarget> { [0] = Target(language: "sv", branchExists: false), [1] = Target(language: "sv", branchExists: false) };

        var translate = Assert.IsType<TranslateOperation>(For(plan, 1, targets)!.Operation);

        Assert.Equal(("sv", true), (translate.Lang, translate.Publish));
    }

    [Fact]
    public void A_create_step_that_updates_existing_content_counts_as_a_set_of_its_values()
    {
        var plan = WritePlan.Parse("""
            {"guidNamespace": "6f1c2b3a-9d4e-4f5a-8b7c-1d2e3f4a5b6c", "operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Page", "properties": {"Heading": "One"}},
              {"op": "publish", "ref": "$page"}
            ]}
            """);
        var existing = new Dictionary<string, int> { ["page"] = 500 };
        var resolved = plan.Steps.Select(s => s with { Operation = WritePlan.Resolve(s, existing) }).ToList();
        var targets = new Dictionary<int, PlanTarget> { [0] = Target(id: 500), [1] = Target(id: 500) };

        var set = Assert.IsType<SetOperation>(PlanSimulation.OnExisting(resolved[1], resolved, targets, existing, updateExisting: true)!.Operation);

        Assert.Equal(("500", "Page", true), (set.Ref, set.Name, set.Publish));
        Assert.Equal("One", (string?)set.Properties!["Heading"]);
    }

    private static readonly WritePlan FromPlan = WritePlan.Parse("""
        {"operations": [
          {"op": "set", "ref": "123", "properties": {"Heading": "Before"}},
          {"op": "set", "ref": "123", "from": "published", "properties": {"Intro": "From published"}},
          {"op": "area", "ref": "123", "property": "MainArea", "action": "add", "item": "456"},
          {"op": "set", "ref": "123", "properties": {"Teaser": "t"}, "publishAt": "2099-01-01T08:00:00Z"},
          {"op": "publish", "ref": "123"}
        ]}
        """);

    [Fact]
    public void A_publish_after_a_step_with_from_is_that_step_and_the_ones_since_on_its_version()
    {
        var targets = Enumerable.Range(0, 5).ToDictionary(i => i, _ => Target());

        var simulation = For(FromPlan, 4, targets)!;

        var set = Assert.IsType<SetOperation>(simulation.Operation);
        // Operation 0's Heading is in a draft the change from the published version leaves out.
        Assert.Equal((FromVersion.Published, true), (set.From, set.Publish));
        Assert.Equal("""{"Intro":"From published","Teaser":"t"}""", set.Properties!.ToJsonString());
        Assert.Equal("456", Assert.Single(set.AreaEdits!).Item);
        Assert.Equal("Dry-run as 123 in 'en' will be after operation(s) 1, 2, 3, published (their changes aren't saved yet), on the published version as operation 1's \"from\" says.",
            Assert.Single(simulation.Notes!));
    }

    [Fact]
    public void Steps_after_one_with_from_build_on_its_result()
    {
        var targets = Enumerable.Range(0, 5).ToDictionary(i => i, _ => Target());

        var area = Assert.IsType<SetOperation>(For(FromPlan, 2, targets)!.Operation);
        Assert.Equal((FromVersion.Published, false), (area.From, area.Publish));
        Assert.Equal("""{"Intro":"From published"}""", area.Properties!.ToJsonString());
        Assert.Equal("456", Assert.Single(area.AreaEdits!).Item);

        var scheduled = For(FromPlan, 3, targets)!;
        var set = Assert.IsType<SetOperation>(scheduled.Operation);
        Assert.Equal((FromVersion.Published, false, new DateTimeOffset(2099, 1, 1, 8, 0, 0, TimeSpan.Zero)), (set.From, set.Publish, set.PublishAt));
        Assert.Contains("after operation(s) 1, 2 and this one, on the published version", Assert.Single(scheduled.Notes!));
    }

    [Fact]
    public void A_step_with_from_and_steps_before_one_are_dry_run_as_they_are()
    {
        var targets = Enumerable.Range(0, 5).ToDictionary(i => i, _ => Target());

        // Its own version is what it starts from; and before any "from", sets aren't folded together.
        Assert.Null(For(FromPlan, 1, targets));
        Assert.Null(For(FromPlan, 0, targets));
        // A ref with a version is checked as given.
        var pinned = WritePlan.Parse("""{"operations": [{"op": "set", "ref": "123", "from": 77, "name": "x"}, {"op": "set", "ref": "123_80", "name": "y"}]}""");
        Assert.Null(For(pinned, 1, new Dictionary<int, PlanTarget> { [0] = Target(), [1] = Target(versioned: true) }));
    }

    [Fact]
    public void From_on_a_branch_the_plan_translates_is_a_usage_error()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "translate", "ref": "123", "lang": "sv", "publish": true},
              {"op": "set", "ref": "123", "lang": "sv", "from": "published", "name": "Nyheter"}
            ]}
            """);
        var targets = new Dictionary<int, PlanTarget> { [0] = Target(language: "sv", branchExists: false), [1] = Target(language: "sv", branchExists: false) };

        var error = Assert.Throws<Core.Errors.UsageException>(() => For(plan, 1, targets));

        Assert.StartsWith("Operation 0 creates the 'sv' branch of 123", error.Message);
    }
}
