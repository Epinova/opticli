using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Seals;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// What a sealed apply runs: creates, changes, moves, translates and publishes by GUID. Nothing that deletes, takes offline,
/// changes access or writes a file, no other people's drafts, no skipped concurrency check, and no ids, which name other
/// content in production.
/// </summary>
public class SealedPlanTests
{
    private const string Page = "61104228-962d-4dd2-925c-b5b12e2373ee";
    private const string Start = "7b08c7d5-7585-47e5-add7-5822da68c3ce";

    private static WritePlan Plan(params string[] steps) =>
        WritePlan.Parse($$$"""{"operations": [{{{string.Join(", ", steps)}}}]}""");

    private static CmsModel Model() => new(
        [new ContentTypeInfo(1, Guid.NewGuid(), "StandardPage", null, null, ContentKind.Page, "Page", null, null)],
        [new PropertyDefinition(10, 1, "Related", "ContentReference", PropertyBaseType.ContentReference, null, false, false),
         new PropertyDefinition(11, 1, "Pages", "ContentReferenceList", PropertyBaseType.Json, null, false, false),
         new PropertyDefinition(12, 1, "Heading", "String", PropertyBaseType.String, null, false, false)],
        [], [], null, null);

    [Fact]
    public void Creating_changing_moving_translating_and_publishing_by_guid_is_sealable()
    {
        var plan = Plan(
            $$$"""{"op": "create", "id": "news", "guid": "71f80142-5378-5061-9f34-b49bc3640495", "parent": "{{{Start}}}", "type": "StandardPage", "name": "News", "properties": {"Related": "{{{Page}}}", "Pages": ["{{{Page}}}"], "Heading": "123", "MainContentArea": [{"ref": "{{{Page}}}"}]}, "publish": true}""",
            $$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "Hi", "Pages": ["$news", "{{{Page}}}"], "MainContentArea": [{"ref": "$news"}]}, "from": "published", "publish": true}""",
            $$$"""{"op": "move", "ref": "$news", "to": "{{{Page}}}"}""",
            $$$"""{"op": "translate", "ref": "$news", "lang": "sv", "name": "Nyheter"}""",
            $$$"""{"op": "publish", "ref": "$news", "lang": "sv"}""");

        Assert.Empty(SealedPlan.Problems(plan, Model()));
        SealedPlan.Require(plan, Model());
    }

    [Theory]
    [InlineData($$$"""{"op": "delete", "ref": "{{{Page}}}"}""", "deletes content")]
    [InlineData($$$"""{"op": "unpublish", "ref": "{{{Page}}}"}""", "takes content offline")]
    [InlineData($$$"""{"op": "access", "ref": "{{{Page}}}", "grant": {"Everyone": "Read"}}""", "access rights")]
    [InlineData($$$"""{"op": "discard", "ref": "{{{Page}}}"}""", "deletes a draft")]
    [InlineData($$$"""{"op": "upload", "file": "x.png", "parent": "{{{Start}}}"}""", "writes a media file")]
    [InlineData($$$"""{"op": "upload", "file": "x.png", "replace": "{{{Page}}}"}""", "writes a media file")]
    [InlineData($$$"""{"op": "translate", "ref": "{{{Page}}}", "lang": "sv", "remove": true, "confirm": true}""", "removes a language branch")]
    [InlineData($$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}, "publish": true, "includeDraft": true}""", "someone else's unpublished changes")]
    [InlineData($$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}, "force": true}""", "skip the check")]
    [InlineData($$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}, "baseVersion": 12}""", "version by its id")]
    [InlineData($$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}, "from": "456"}""", "version by its id")]
    [InlineData($$$"""{"op": "publish", "ref": "{{{Page}}}", "version": 456}""", "version by its id")]
    [InlineData($$$"""{"op": "create", "parent": "{{{Start}}}", "type": "StandardPage", "name": "Anonymous"}""", "has no GUID")]
    [InlineData("""{"op": "set", "ref": "10", "properties": {"Heading": "x"}}""", "refers to '10'")]
    [InlineData("""{"op": "set", "ref": "/en/about-us/", "properties": {"Heading": "x"}}""", "refers to '/en/about-us/'")]
    [InlineData($$$"""{"op": "move", "ref": "{{{Page}}}", "to": "5"}""", "refers to '5'")]
    [InlineData($$$"""{"op": "area", "ref": "{{{Page}}}", "property": "MainContentArea", "action": "add", "item": "42"}""", "refers to '42'")]
    [InlineData($$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"MainContentArea": [{"ref": "42"}]}}""", "properties.MainContentArea[0].ref is '42'")]
    [InlineData($$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Related": "17"}}""", "properties.Related is '17'")]
    [InlineData($$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Pages": ["{{{Page}}}", "17_3"]}}""", "properties.Pages[1] is '17_3'")]
    [InlineData($$$$"""{"op": "set", "ref": "{{{{Page}}}}", "properties": {"Block": {"Related": 17}}}""", "properties.Block.Related is '17'")]
    [InlineData($$$"""{"op": "create", "id": "x", "parent": "{{{Start}}}", "type": "", "blueprint": "Campaign", "name": "X"}""", "blueprint 'Campaign'")]
    public void Anything_else_is_refused_and_listed(string step, string expected)
    {
        var plan = Plan(step);

        var refused = Assert.Throws<RefusedException>(() => SealedPlan.Require(plan, Model()));

        Assert.Equal(SealedPlan.Reason, SealStoreTests.Reason(refused));
        Assert.Contains(SealedPlan.Problems(plan, Model()), p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void An_expiry_that_has_passed_is_counted_for_the_user_to_see()
    {
        var plan = Plan(
            $$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"StopPublish": "2020-01-01T00:00:00Z"}, "publish": true}""",
            $$$"""{"op": "create", "id": "x", "guid": "71f80142-5378-5061-9f34-b49bc3640495", "parent": "{{{Start}}}", "type": "StandardPage", "name": "X", "properties": {"pageStopPublish": "2030-05-01 10:00"}}""",
            $$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"StopPublish": null}, "publish": true}""");
        var production = new ProductionState("m", []);
        var options = new SealOptions(UpdateExisting: false, Publish: false, RequestApproval: false);

        Assert.Empty(SealedPlan.Problems(plan, Model()));
        Assert.Equal(1, SealSummary.Of(plan, options, production, now: new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero)).Expiring);
        // Counted again when the plan runs: by then an expiry may have passed.
        Assert.Equal(2, SealSummary.Of(plan, options, production, now: new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero)).Expiring);
    }

    [Fact]
    public void Every_problem_is_listed_at_once_and_ids_in_values_need_the_model()
    {
        var plan = Plan(
            $$$"""{"op": "delete", "ref": "{{{Page}}}"}""",
            $$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Related": "17"}}""",
            """{"op": "publish", "ref": "12"}""");

        Assert.Equal(3, SealedPlan.Problems(plan, Model()).Count);
        // Without the model only a "ref" in a value is known to be a reference.
        Assert.Equal(2, SealedPlan.Problems(plan, model: null).Count);
    }

    [Fact]
    public void Touched_content_is_what_the_steps_write_and_the_rest_is_only_named()
    {
        var other = "42cfb67a-6c8c-4253-8ff0-366d442a30cc";
        var plan = Plan(
            $$$"""{"op": "create", "id": "news", "guid": "71f80142-5378-5061-9f34-b49bc3640495", "parent": "{{{Start}}}", "type": "StandardPage", "name": "News"}""",
            $$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}}""",
            $$$"""{"op": "move", "ref": "{{{other}}}", "to": "$news"}""",
            $$$"""{"op": "area", "ref": "{{{Page}}}", "property": "MainContentArea", "action": "add", "item": "{{{other}}}"}""");

        Assert.Equal([Guid.Parse("71f80142-5378-5061-9f34-b49bc3640495"), Guid.Parse(Page), Guid.Parse(other)], SealedPlan.Touched(plan));
        Assert.Equal([Guid.Parse(Start)], SealedPlan.Referenced(plan));
    }

    [Fact]
    public void The_summary_counts_steps_types_publishes_and_what_production_has()
    {
        var plan = Plan(
            $$$"""{"op": "create", "id": "news", "guid": "71f80142-5378-5061-9f34-b49bc3640495", "parent": "{{{Start}}}", "type": "StandardPage", "name": "News", "publish": true}""",
            $$$"""{"op": "block", "id": "teaser", "guid": "0d6f3a52-8b1e-4c7a-9e25-5a4c1b7d3f90", "for": "$news", "type": "TeaserBlock", "name": "Teaser"}""",
            $$$"""{"op": "set", "ref": "{{{Page}}}", "properties": {"Heading": "x"}}""");
        var news = plan.Steps[0].Operation.ContentGuid!.Value;
        var production = new ProductionState("m", [new SealedItem(news, true, false, null, "StandardPage", "News", []), new SealedItem(Guid.Parse(Page), true, false, null, "StandardPage", "About us", [])]);

        var summary = SealSummary.Of(plan, new SealOptions(UpdateExisting: true, Publish: false, RequestApproval: false), production);
        var published = SealSummary.Of(plan, new SealOptions(UpdateExisting: true, Publish: true, RequestApproval: false), production);

        Assert.Equal((3, 1, 1, 1, 2), (summary.Steps, summary.Publishes, summary.Existing, summary.New, summary.Touched));
        Assert.Equal(new Dictionary<string, int> { ["block"] = 1, ["create"] = 1, ["set"] = 1 }, summary.Operations);
        Assert.Equal(new Dictionary<string, int> { ["StandardPage"] = 1, ["TeaserBlock"] = 1 }, summary.Types);
        Assert.Equal(3, published.Publishes);
    }
}
