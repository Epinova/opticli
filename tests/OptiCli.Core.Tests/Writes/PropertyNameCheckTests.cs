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
