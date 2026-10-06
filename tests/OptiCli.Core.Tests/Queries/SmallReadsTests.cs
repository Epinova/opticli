using OptiCli.Core.Queries;

namespace OptiCli.Core.Tests.Queries;

/// <summary>The parts of <c>categories</c> and <c>history</c> that don't need a database.</summary>
public class SmallReadsTests
{
    private static CategoryReader.CategoryRow Row(int id, int? parent, string name, int sort = 0, bool selectable = true) =>
        new(id, parent, Guid.Empty, sort, Available: true, selectable, name, $"{name} (shown)", Items: 0);

    [Fact]
    public void Categories_are_listed_depth_first_in_admin_modes_order_without_the_CMSs_root()
    {
        var tree = CategoryReader.Tree(
        [
            Row(1, null, "Root"),
            Row(5, 1, "Topics", sort: 2),
            Row(2, 1, "Regions", sort: 1),
            Row(6, 5, "Sports"),
            Row(3, 2, "North", sort: 2),
            Row(4, 2, "South", sort: 1, selectable: false),
        ]);

        Assert.Equal(
            [("Regions", (int?)null, "Regions", 0), ("South", 2, "Regions/South", 1), ("North", 2, "Regions/North", 1), ("Topics", null, "Topics", 0), ("Sports", 5, "Topics/Sports", 1)],
            tree.Select(c => (c.Name, c.Parent, c.Path, c.Depth)));
        Assert.False(tree.Single(c => c.Name == "South").Selectable);
    }

    [Fact]
    public void A_category_tree_with_a_loop_still_ends()
    {
        var tree = CategoryReader.Tree([Row(1, null, "Root"), Row(2, 1, "A"), Row(3, 2, "B"), Row(2, 3, "A again")]);

        Assert.Equal(["A", "B"], tree.Select(c => c.Name));
    }

    [Fact]
    public void A_change_log_entry_is_read_from_its_properties_element()
    {
        var data = HistoryReader.Attributes("""<properties ContentLink="1577_88" Language="en" Name="News &amp; events" OldParent="224" NewParent="2" />""");

        Assert.Equal(("1577_88", "en", "News & events", "224", "2"), (data["ContentLink"], data["Language"], data["Name"], data["OldParent"], data["NewParent"]));
        Assert.Empty(HistoryReader.Attributes("not xml"));
        Assert.Empty(HistoryReader.Attributes(null));
    }

    [Theory]
    [InlineData(2, "publish")]
    [InlineData(3, HistoryActions.DeletePermanently)]
    [InlineData(5, HistoryActions.Move)]
    [InlineData(13, "deleteVersion")]
    [InlineData(99, "unknown")]
    public void Change_log_actions_are_named_as_the_CMS_numbers_them(int value, string name)
    {
        Assert.Equal(name, HistoryActions.Name(value));
    }

    [Fact]
    public void The_change_log_names_content_by_provider_and_id()
    {
        Assert.Equal("content://default/123", HistoryReader.RelatedItem(123));
        Assert.Equal("content://images/63", HistoryReader.RelatedItem(63, "images"));
    }
}
