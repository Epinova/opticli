using System.Text.Json;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

public class AreaItemRulesTests
{
    private const string Members = "44444444-4444-4444-4444-444444444444";
    private const string Visitors = "55555555-5555-5555-5555-555555555555";

    private static readonly AreaItemRules.Option[] Options =
    [
        new("full", "Full width", "displaymode-full"),
        new("wide", "Wide", "displaymode-wide"),
        new("half", "Half", "displaymode-half"),
    ];

    [Fact]
    public void Items_keep_the_settings_of_the_same_content_when_others_are_inserted_or_removed() =>
        Assert.Equal([-1, 0, 2, -1], AreaItemRules.Match(["123", "456", "789"], ["999", "123", "789", "321"]));

    [Fact]
    public void Reordered_items_keep_their_own_settings() =>
        Assert.Equal([2, 0, 1], AreaItemRules.Match(["123", "456", "789"], ["789", "123", "456"]));

    [Fact]
    public void The_nth_item_for_some_content_takes_over_the_nth_current_one() =>
        // An item inserted before two for the same content doesn't swap their visitor groups.
        Assert.Equal([-1, 0, 1, -1], AreaItemRules.Match(["123", "123"], ["456", "123", "123", "123"]));

    [Fact]
    public void Inline_blocks_and_content_providers_match_by_their_ref()
    {
        Assert.Equal([-1, 1], AreaItemRules.Match([null, "63__dam"], [null, "63__DAM"]));
        Assert.Empty(AreaItemRules.Match(["123"], []));
        Assert.Equal([-1], AreaItemRules.Match([], ["123"]));
    }

    [Fact]
    public void Personalization_left_out_is_kept()
    {
        var (group, visitorGroups) = AreaItemRules.Personalization(Item("""{"ref": "123"}"""), "g1", [Members]);

        Assert.Equal("g1", group);
        Assert.Equal([Members], visitorGroups);
    }

    [Fact]
    public void Personalization_given_replaces_the_current_one_field_by_field()
    {
        var (group, visitorGroups) = AreaItemRules.Personalization(Item($$"""{"ref": "123", "visitorGroups": ["{{Visitors}}", " {{Visitors}} ", ""]}"""), "g1", [Members]);

        Assert.Equal("g1", group);
        Assert.Equal([Visitors], visitorGroups);
    }

    [Fact]
    public void Empty_values_remove_personalization()
    {
        var (group, visitorGroups) = AreaItemRules.Personalization(Item("""{"ref": "123", "group": "", "visitorGroups": []}"""), "g1", [Members]);

        Assert.Null(group);
        Assert.Empty(visitorGroups);
    }

    [Fact]
    public void A_new_item_has_none_unless_given()
    {
        var none = AreaItemRules.Personalization(Item("""{"ref": "123"}"""), null, null);
        Assert.Null(none.Group);
        Assert.Empty(none.VisitorGroups);

        var (group, visitorGroups) = AreaItemRules.Personalization(Item($$"""{"ref": "123", "group": "g2", "visitorGroups": ["{{Members}}"]}"""), null, null);
        Assert.Equal("g2", group);
        Assert.Equal([Members], visitorGroups);
    }

    [Theory]
    [InlineData("wide", "wide")]
    [InlineData(" Wide ", "wide")]
    [InlineData("Full width", "full")]
    [InlineData("displaymode-half", "half")]
    public void Display_options_are_named_by_id_name_or_tag(string input, string id) =>
        Assert.Equal(id, AreaItemRules.DisplayOption(input, Options));

    [Fact]
    public void An_unknown_display_option_is_refused_with_a_suggestion()
    {
        var error = Assert.Throws<AgentException>(() => AreaItemRules.DisplayOption("fulll", Options));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
        Assert.Equal("No display option 'fulll'.", error.Message);
        Assert.Equal("Did you mean full? Display options: full, wide, half.", error.Hint);
    }

    [Fact]
    public void A_site_without_display_options_takes_none()
    {
        var error = Assert.Throws<AgentException>(() => AreaItemRules.DisplayOption("wide", []));

        Assert.Contains("registers none", error.Message);
    }

    private static AreaItemValue Item(string json) => JsonSerializer.Deserialize<AreaItemValue>(json, AgentJson.Options)!;
}
