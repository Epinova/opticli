using OptiCli.Agent.Content;

namespace OptiCli.Agent.Tests.Content;

public class ContentPlacementTests
{
    private static string? Problem(PlacementKind child, PlacementKind parent, bool allowed = true) =>
        ContentPlacement.Problem(child, "ChildType", parent, "ParentType", 45, allowed);

    // Theory data names the kinds: PlacementKind is internal to the agent.
    private static string? Problem(string child, string parent, bool allowed = true) =>
        Problem(Enum.Parse<PlacementKind>(child), Enum.Parse<PlacementKind>(parent), allowed);

    [Theory]
    [InlineData("Page", "Page")]
    [InlineData("Block", "Folder")]
    [InlineData("Media", "Folder")]
    [InlineData("Folder", "Folder")]
    [InlineData("Other", "Page")]
    [InlineData("Block", "Other")]
    public void Pages_go_below_pages_and_assets_in_folders(string child, string parent) => Assert.Null(Problem(child, parent));

    [Theory]
    [InlineData("Block")]
    [InlineData("Media")]
    [InlineData("Folder")]
    public void Blocks_media_and_folders_are_refused_below_a_page(string child)
    {
        var problem = Problem(child, "Page")!;

        Assert.Contains("asset folders, not below ParentType (45), a page", problem);
        Assert.Contains("--for <page>", problem);
    }

    [Fact]
    public void A_page_is_refused_in_an_asset_folder() =>
        Assert.Equal("ChildType is a page type; pages go in the page tree, not below ParentType (45), an asset folder.", Problem("Page", "Folder"));

    [Theory]
    [InlineData("Block", "a block")]
    [InlineData("Media", "a media item")]
    public void Blocks_and_media_have_no_children(string parent, string what) =>
        Assert.Equal($"ParentType (45) is {what}, which can't have children.", Problem("Folder", parent));

    [Theory]
    [InlineData("Page", "Page")]
    [InlineData("Block", "Folder")]
    public void The_parent_types_availability_decides_the_rest(string child, string parent) =>
        Assert.Equal("ChildType is not allowed below ParentType (45).", Problem(child, parent, allowed: false));

    [Fact]
    public void The_structural_rule_is_reported_before_availability() =>
        Assert.Contains("a page type", Problem("Page", "Folder", allowed: false));
}
