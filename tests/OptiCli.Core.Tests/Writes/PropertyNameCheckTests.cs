using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Tests.Content;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class PropertyNameCheckTests
{
    private static readonly OptiCli.Core.Content.CmsModel Model = ModelFixture.Create();

    [Fact]
    public void Known_properties_nested_block_properties_and_built_ins_pass()
    {
        var properties = JsonNode.Parse("""{"heading": "x", "Hero": {"SubHeading": "y"}, "Name": "n", "PageURLSegment": "s", "MainArea": []}""")!.AsObject();

        PropertyNameCheck.Check(Model, ModelFixture.ArticlePage, properties);
    }

    [Fact]
    public void An_unknown_property_suggests_the_closest_name()
    {
        var error = Assert.Throws<UsageException>(() => PropertyNameCheck.Check(Model, ModelFixture.ArticlePage, new JsonObject { ["Headng"] = "x" }));

        Assert.Contains("'Headng' is not a property of ArticlePage", error.Message);
        Assert.StartsWith("Did you mean Heading?", error.Hint);
        Assert.Contains("MainBody", error.Hint);
    }

    [Fact]
    public void An_unknown_nested_property_names_the_block_type()
    {
        var error = Assert.Throws<UsageException>(() => PropertyNameCheck.Check(Model, ModelFixture.ArticlePage,
            new JsonObject { ["Hero"] = new JsonObject { ["SubHeadng"] = "x" } }));

        Assert.Contains("'Hero.SubHeadng' is not a property of HeroBlock", error.Message);
        Assert.StartsWith("Did you mean SubHeading?", error.Hint);
    }

    [Fact]
    public void ContentArea_items_from_get_go_to_the_agent_as_set_takes_them()
    {
        // As get shows them: a shared block with what get adds about it, an inline block with its values in {type, value}.
        var properties = JsonNode.Parse("""
            {"MainArea": [
              {"ref": "123", "guid": "6a18c2dd-c55a-40d4-aa35-4586885bbfbb", "type": "TeaserBlock", "name": "Shared", "status": "published", "displayOption": "wide", "visitorGroupNames": ["x"], "renderSettings": {"id": "a"}},
              {"inline": true, "type": "teaserblock", "name": "Intro", "properties": {"Text": {"type": "XhtmlString", "value": "<p>Hi</p>"}, "StartDate": "2025-01-31"}, "group": "g", "visitorGroups": ["44444444-4444-4444-4444-444444444444"], "renderSettings": {"id": "b"}},
              {"type": "TeaserBlock"}
            ]}
            """)!.AsObject();

        var prepared = PropertyNameCheck.Prepare(Model, ModelFixture.ArticlePage, properties)!;

        Assert.Equal("""[{"ref":"123","guid":"6a18c2dd-c55a-40d4-aa35-4586885bbfbb","displayOption":"wide"},"""
            + """{"type":"TeaserBlock","name":"Intro","properties":{"Text":"\u003Cp\u003EHi\u003C/p\u003E","StartDate":"2025-01-31"},"group":"g","visitorGroups":["44444444-4444-4444-4444-444444444444"]},"""
            + """{"type":"TeaserBlock"}]""", prepared["MainArea"]!.ToJsonString());
        // The input is left as it was.
        Assert.Equal("published", (string?)properties["MainArea"]![0]!["status"]);
    }

    [Theory]
    [InlineData("""[{"ref": "123", "inline": true}]""", "'MainArea[0]' gives both a shared block")]
    [InlineData("""[{"ref": "123", "properties": {}}]""", "'MainArea[0]' gives both a shared block")]
    [InlineData("""[{"displayOption": "wide"}]""", "'MainArea[0]' names no block")]
    [InlineData("""[{"inline": true}]""", "'MainArea[0]' is an inline block without its type")]
    [InlineData("""[{"type": "ArticlePage"}]""", "MainArea[0]: ArticlePage is a page type, not a block type")]
    [InlineData("""[{"type": "TeaserBlok"}]""", "MainArea[0]: No content type 'TeaserBlok'")]
    [InlineData("""[{"type": "TeaserBlock", "properties": {"Txt": "x"}}]""", "'MainArea[0].Txt' is not a property of TeaserBlock")]
    [InlineData("""["123"]""", "'MainArea[0]' must be an object")]
    public void A_ContentArea_item_that_is_neither_kind_or_both_is_refused(string items, string message)
    {
        var error = Assert.Throws<UsageException>(() => PropertyNameCheck.Prepare(Model, ModelFixture.ArticlePage, JsonNode.Parse($$"""{"MainArea": {{items}}}""")!.AsObject()));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void A_position_names_an_inline_block_of_a_ContentArea_whose_values_the_site_checks()
    {
        var prepared = PropertyNameCheck.Prepare(Model, ModelFixture.ArticlePage, JsonNode.Parse("""{"MainArea[2]": {"Anything": {"type": "String", "value": "x"}}}""")!.AsObject())!;
        Assert.Equal("""{"MainArea[2]":{"Anything":"x"}}""", prepared.ToJsonString());

        Assert.Contains("'Heading' is a String property", Assert.Throws<UsageException>(() =>
            PropertyNameCheck.Prepare(Model, ModelFixture.ArticlePage, JsonNode.Parse("""{"Heading[0]": {"x": "y"}}""")!.AsObject())).Message);
        Assert.Contains("'MainArea[2]' takes the inline block's values", Assert.Throws<UsageException>(() =>
            PropertyNameCheck.Prepare(Model, ModelFixture.ArticlePage, JsonNode.Parse("""{"MainArea[2]": "x"}""")!.AsObject())).Message);
        Assert.Contains("'MainAre' is not a property of ArticlePage", Assert.Throws<UsageException>(() =>
            PropertyNameCheck.Prepare(Model, ModelFixture.ArticlePage, JsonNode.Parse("""{"MainAre[2]": {}}""")!.AsObject())).Message);
    }

    [Fact]
    public void Built_in_names_are_only_allowed_at_the_top_level()
    {
        Assert.Throws<UsageException>(() => PropertyNameCheck.Check(Model, ModelFixture.ArticlePage,
            new JsonObject { ["Hero"] = new JsonObject { ["Name"] = "x" } }));
    }

    [Fact]
    public void Content_area_lookup_returns_the_exact_name_and_rejects_other_types()
    {
        Assert.Equal("MainArea", PropertyNameCheck.RequireContentArea(Model, ModelFixture.ArticlePage, "mainarea").Name);

        var error = Assert.Throws<UsageException>(() => PropertyNameCheck.RequireContentArea(Model, ModelFixture.ArticlePage, "Heading"));
        Assert.Contains("not a ContentArea", error.Message);
        Assert.Contains("MainArea", error.Hint);
    }

    [Fact]
    public void Block_list_items_are_checked_against_the_item_type()
    {
        PropertyNameCheck.Check(Model, ModelFixture.ArticlePage, JsonNode.Parse("""{"Facts": [{"Label": "a"}, {"label": "b", "Sources": ["1"]}]}""")!.AsObject());

        var typo = Assert.Throws<UsageException>(() => PropertyNameCheck.Check(Model, ModelFixture.ArticlePage,
            JsonNode.Parse("""{"Facts": [{"Label": "a"}, {"Lable": "b"}]}""")!.AsObject()));
        Assert.Contains("'Facts[1].Lable' is not a property of FactBlock", typo.Message);

        var notAnObject = Assert.Throws<UsageException>(() => PropertyNameCheck.Check(Model, ModelFixture.ArticlePage,
            JsonNode.Parse("""{"Facts": ["just text"]}""")!.AsObject()));
        Assert.Contains("'Facts[0]' must be an object of FactBlock property names", notAnObject.Message);
    }

    [Fact]
    public void Publish_dates_are_built_in()
    {
        PropertyNameCheck.Check(Model, ModelFixture.ArticlePage,
            JsonNode.Parse("""{"StartPublish": "2025-02-14", "StopPublish": null, "PageStartPublish": "2025-02-14T08:00:00+01:00"}""")!.AsObject());
    }

    [Fact]
    public void Page_sorting_is_built_in()
    {
        PropertyNameCheck.Check(Model, ModelFixture.ArticlePage,
            JsonNode.Parse("""{"ChildSortOrder": "PublishedDescending", "SortIndex": 200, "PageChildOrderRule": 8, "pagePeerOrder": "100"}""")!.AsObject());
    }
}
