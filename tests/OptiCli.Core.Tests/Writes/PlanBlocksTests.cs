using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>
/// The blocks a plan's area adds make where the site makes new blocks in a ContentArea shared: an <c>id</c> for later
/// steps to reach them by, and the dry run's checks of steps that would reach them by position.
/// </summary>
public class PlanBlocksTests
{
    private static readonly WritePlan Plan = WritePlan.Parse("""
        {"operations": [
          {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Page"},
          {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "type": "TeaserBlock", "id": "teaser", "name": "Teaser", "values": {"Heading": "One", "Text": "t"}},
          {"op": "set", "ref": "$teaser", "properties": {"Heading": "Two"}},
          {"op": "area", "ref": "200", "property": "RelatedArea", "action": "add", "item": "$teaser"},
          {"op": "publish", "ref": "$teaser"}
        ]}
        """);

    private static readonly Dictionary<string, int> None = [];

    [Fact]
    public void An_area_add_with_a_type_names_its_block_for_later_steps()
    {
        Assert.True(PlanBlocks.Names(Plan.Steps[1].Operation));
        Assert.Equal(["teaser"], Plan.Steps[2].DependsOn);
        Assert.Equal(["teaser"], Plan.Steps[3].DependsOn);
        Assert.Equal("TeaserBlock", PlanSimulation.TypeOf("teaser", Plan.Steps));

        // Once it ran, its block's ref goes in as for content a create made.
        var resolved = WritePlan.Resolve(Plan.Steps[2], new Dictionary<string, int> { ["page"] = 300, ["teaser"] = 301 });
        Assert.Equal("301", ((SetOperation)resolved).Ref);
    }

    [Theory]
    [InlineData("""{"op": "area", "ref": "1", "property": "MainArea", "action": "add", "item": "2", "id": "x"}""")]
    [InlineData("""{"op": "area", "ref": "1", "property": "MainArea", "action": "remove", "index": 0, "id": "x"}""")]
    [InlineData("""{"op": "area", "ref": "1", "property": "MainArea", "action": "set", "index": 0, "values": {"A": 1}, "id": "x"}""")]
    public void Only_an_add_with_a_type_takes_an_id(string step) =>
        Assert.Contains("\"id\" names the block an add with \"type\" makes", Assert.Throws<UsageException>(() => WritePlan.Parse($$"""{"operations": [{{step}}]}""")).Message);

    [Fact]
    public void A_ref_to_an_id_no_step_gives_names_area_adds_in_the_problem() =>
        Assert.Contains("not the id of an earlier create, block, upload or area add operation",
            Assert.Throws<UsageException>(() => WritePlan.Parse("""{"operations": [{"op": "set", "ref": "$nothing", "properties": {"A": 1}}]}""")).Message);

    [Fact]
    public void A_step_on_the_named_block_is_dry_run_as_a_block_in_the_pages_folder_with_every_value_set_on_it()
    {
        var simulation = PlanSimulation.For(Plan.Steps[2], Plan.Steps, None, updateExisting: false)!;

        var block = Assert.IsType<BlockCreateOperation>(simulation.Operation);
        Assert.Equal(("TeaserBlock", "Teaser", "100", "Two", "t"), (block.Type, block.Name, block.For, (string?)block.Properties!["Heading"], (string?)block.Properties["Text"]));
        Assert.Equal("teaser", simulation.Target);
        Assert.Contains("operation 1 adds to MainArea", Assert.Single(simulation.Notes!));
        // A publish of it, as the block published.
        Assert.True(Assert.IsType<BlockCreateOperation>(PlanSimulation.For(Plan.Steps[4], Plan.Steps, None, updateExisting: false)!.Operation).Publish);
        // On an existing page: in that page's folder.
        var onExisting = WritePlan.Parse("""
            {"operations": [
              {"op": "area", "ref": "300", "property": "MainArea", "action": "add", "type": "TeaserBlock", "id": "teaser"},
              {"op": "set", "ref": "$teaser", "properties": {"Heading": "Two"}}
            ]}
            """);
        var inFolder = Assert.IsType<BlockCreateOperation>(PlanSimulation.For(onExisting.Steps[1], onExisting.Steps, None, updateExisting: false)!.Operation);
        Assert.Equal(("300", "TeaserBlock (planned)"), (inFolder.For, inFolder.Name));
    }

    private static WriteOutput Output(params SharedBlockResult[] shared) =>
        new(null, null, null, null, null, null, null, null, false, false, true, true, null, [], null) { SharedBlocks = shared };

    private static SharedBlockResult Block(string path, string? reference, string action = SharedBlockActions.Created) =>
        new() { Path = path, Ref = reference, Name = "n", Type = "TeaserBlock", Action = action };

    [Fact]
    public void The_block_an_add_made_is_the_one_entry_for_an_item_of_its_area()
    {
        var add = new AreaEdit("1", "MainArea", AreaOps.Add) { Type = "TeaserBlock" };

        // The block it made, not one inside it or one published along.
        Assert.Equal(501, PlanBlocks.MadeBlock(add, Output(Block("MainArea[2].Area[0]", "500_1"), Block("MainArea[2]", "501_2"), Block("MainArea[0]", "502_3", SharedBlockActions.Published))));
        // Run again: the one found there.
        Assert.Equal(503, PlanBlocks.MadeBlock(add, Output(Block("MainArea[1]", "503_4", SharedBlockActions.Existing))));
        Assert.Null(PlanBlocks.MadeBlock(add, Output()));
        Assert.Null(PlanBlocks.MadeBlock(add with { Type = null, Item = "9" }, Output(Block("MainArea[1]", "503_4"))));
        Assert.Equal(504, PlanBlocks.MadeBlock(new AreaEdit("1", "MainArea[0].Area", AreaOps.Add) { Type = "TeaserBlock" }, Output(Block("MainArea[0].Area[1]", "504_1"))));
    }

    [Fact]
    public void A_step_that_changes_a_planned_shared_block_by_its_position_is_refused_in_the_dry_run()
    {
        var output = Output(Block("MainArea[1]", null), Block("MainArea[0]", "77_1", SharedBlockActions.Updated));
        var set = new SetOperation("1", System.Text.Json.Nodes.JsonNode.Parse("""{"MainArea[1]": {"Heading": "x"}}""")!.AsObject());

        var refused = PlanBlocks.OnPlannedBlock(set, output);

        Assert.NotNull(refused);
        Assert.Contains("MainArea[1] names by its position the block an earlier operation adds", refused.Message);
        Assert.Contains("\"$<id>\"", refused.Hint);
        Assert.NotNull(PlanBlocks.OnPlannedBlock(new AreaEdit("1", "MainArea", AreaOps.Set, Index: 1) { Values = [] }, output));
        Assert.NotNull(PlanBlocks.OnPlannedBlock(new AreaEdit("1", "MainArea[1].Area", AreaOps.Add) { Type = "TeaserBlock" }, output));
        // A block that exists already is the agent's to refuse; moving or removing a new one by position works.
        Assert.Null(PlanBlocks.OnPlannedBlock(new SetOperation("1", System.Text.Json.Nodes.JsonNode.Parse("""{"MainArea[0]": {"Heading": "x"}}""")!.AsObject()), output));
        Assert.Null(PlanBlocks.OnPlannedBlock(new AreaEdit("1", "MainArea", AreaOps.Move, Index: 1, To: 0), output));
        Assert.Null(PlanBlocks.OnPlannedBlock(new AreaEdit("1", "MainArea", AreaOps.Remove, Index: 1), output));
        // Where the site makes inline blocks, a dry run reports none.
        Assert.Null(PlanBlocks.OnPlannedBlock(set, Output()));
    }

    [Fact]
    public void A_later_step_on_a_block_an_add_left_inline_fails_with_why()
    {
        var operation = WritePlan.Resolve(Plan.Steps[2], new Dictionary<string, int> { ["page"] = 300 });

        var refused = PlanBlocks.Unresolved(operation, Plan.Steps, Plan.Steps[2].Index);

        Assert.NotNull(refused);
        Assert.Contains("'$teaser' is the block operation 1 adds to MainArea", refused.Message);
        Assert.Contains("by its position", refused.Hint);
        Assert.NotNull(PlanBlocks.Unresolved(WritePlan.Resolve(Plan.Steps[3], new Dictionary<string, int> { ["page"] = 300 }), Plan.Steps, 3));
        Assert.Null(PlanBlocks.Unresolved(WritePlan.Resolve(Plan.Steps[2], new Dictionary<string, int> { ["page"] = 300, ["teaser"] = 301 }), Plan.Steps, 2));
    }

    [Fact]
    public void The_cli_passes_the_shared_blocks_of_a_write_and_those_a_failed_write_made_on()
    {
        var made = new[] { Block("MainArea[0]", "12_34") };
        var result = new WriteResult { Saved = true, Changes = [], SharedBlocks = made };

        Assert.Same(made, WriteOutput.From(result).SharedBlocks);
        Assert.Null(WriteOutput.From(result with { SharedBlocks = [] }).SharedBlocks);
        var error = AgentErrors.ToException(new AgentError(AgentErrorCodes.Internal, "The save failed.", "the database") { MadeBlocks = made });
        Assert.Same(made, Assert.IsType<AgentErrorDetails>(error.Details).MadeBlocks);
    }
}
