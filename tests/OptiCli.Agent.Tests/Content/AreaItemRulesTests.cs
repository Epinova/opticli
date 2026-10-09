using System.Text.Json;
using OptiCli.Cms;
using OptiCli.Cms.Content;
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
    public void An_inline_block_that_isnt_a_copy_takes_over_a_current_one_only_when_it_is_the_only_one_of_its_type_on_both_sides()
    {
        var teaser = AreaItemRules.InlineKey(11);
        var button = AreaItemRules.InlineKey(12);

        // One teaser and one button on each side: unambiguous.
        Assert.Equal([1, -1, 0], AreaItemRules.Match([button, teaser, "123"], [teaser, "456", button]));
        // Two teasers on either side: which one was meant can't be told, so neither takes one over.
        Assert.Equal([-1, -1], AreaItemRules.Match([teaser, teaser], [teaser, teaser]));
        Assert.Equal([-1], AreaItemRules.Match([teaser, teaser], [teaser]));
        Assert.Equal([-1, -1], AreaItemRules.Match([teaser], [teaser, teaser]));
        // A ref never pairs with an inline block, whatever the ids.
        Assert.Equal([-1], AreaItemRules.Match([AreaItemRules.InlineKey(123)], ["123"]));
    }

    [Fact]
    public void An_inline_block_given_as_it_is_pairs_with_itself_first_and_only_an_unambiguous_rest_pairs_after()
    {
        var teaser = AreaItemRules.InlineKey(11);
        bool Same(int i, int j, params (int, int)[] pairs) => pairs.Contains((i, j));

        // Current: A, B, C; written: A as it is, C edited (B left out). A pairs exactly; C and B are both left over: ambiguous.
        Assert.Equal([new AreaItemRules.Pairing(0, true), new AreaItemRules.Pairing(-1, false)],
            AreaItemRules.Pair([teaser, teaser, teaser], [teaser, teaser], (i, j) => Same(i, j, (0, 0))));
        // Current: A, B; written: B as it is, A edited: A is the only one left on both sides, so it keeps A's settings.
        Assert.Equal([new AreaItemRules.Pairing(1, true), new AreaItemRules.Pairing(0, false)],
            AreaItemRules.Pair([teaser, teaser], [teaser, teaser], (i, j) => Same(i, j, (0, 1))));
        // Exact copies, moved: each pairs with itself.
        Assert.Equal([2, 0, 1], AreaItemRules.Match([teaser, teaser, teaser], [teaser, teaser, teaser], (i, j) => Same(i, j, (0, 2), (1, 0), (2, 1))));
        // A copy is only of an item with the same key.
        Assert.Equal([-1], AreaItemRules.Match(["123"], [teaser], (_, _) => true));
    }

    [Fact]
    public void Of_exact_copies_one_with_the_same_name_and_display_option_pairs_first()
    {
        var teaser = AreaItemRules.InlineKey(11);

        // Both current blocks are copies of the new item; it names the second one.
        Assert.Equal([new AreaItemRules.Pairing(1, true)], AreaItemRules.Pair([teaser, teaser], [teaser], (_, _) => true, (_, j) => j == 1));
        // Without one alike, the first copy.
        Assert.Equal([new AreaItemRules.Pairing(0, true)], AreaItemRules.Pair([teaser, teaser], [teaser], (_, _) => true, (_, _) => false));
    }

    [Theory]
    [InlineData("MainArea[2]", "MainArea", 2)]
    [InlineData("MainArea[0]", "MainArea", 0)]
    public void A_name_with_a_position_names_one_item_of_a_ContentArea(string name, string property, int index) =>
        Assert.Equal((property, index), AreaItemRules.Indexed(name));

    [Theory]
    [InlineData("MainArea[x]")]
    [InlineData("MainArea[-1]")]
    [InlineData("[2]")]
    [InlineData("MainArea]")]
    public void Brackets_without_a_position_are_a_usage_error(string name)
    {
        Assert.Null(AreaItemRules.Indexed("MainArea"));
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => AreaItemRules.Indexed(name)).Code);
    }

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
