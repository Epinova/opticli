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
}
