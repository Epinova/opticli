using OptiCli.Core.Properties;

namespace OptiCli.Core.Tests.Properties;

public class ScopePathTests
{
    [Fact]
    public void Local_block_value()
    {
        var scope = ScopePath.Parse(".104.301.")!;

        Assert.Equal([new ScopeStep(104, null, null)], scope.Steps);
        Assert.Equal(301, scope.LeafPropertyId);
    }

    [Fact]
    public void Block_list_item()
    {
        var scope = ScopePath.Parse(".105(2).401.")!;

        Assert.Equal([new ScopeStep(105, null, 2)], scope.Steps);
        Assert.Equal(401, scope.LeafPropertyId);
    }

    [Fact]
    public void Inline_block_in_a_content_area_nested_two_deep()
    {
        var scope = ScopePath.Parse(".103:20(0).150:30(1).301.")!;

        Assert.Equal([new ScopeStep(103, 20, 0), new ScopeStep(150, 30, 1)], scope.Steps);
        Assert.Equal(301, scope.LeafPropertyId);
    }

    [Theory]
    [InlineData(".105(1).106(0)", new[] { 105 }, 106, 0)]
    [InlineData(".106(3)", new int[0], 106, 3)]
    public void Item_of_a_list_of_plain_values_has_an_index_on_its_own_segment(string scopeName, int[] containers, int leaf, int index)
    {
        var scope = ScopePath.Parse(scopeName)!;

        Assert.Equal(containers, scope.Steps.Select(s => s.PropertyId));
        Assert.Equal(leaf, scope.LeafPropertyId);
        Assert.Equal(index, scope.LeafIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".101.")]
    [InlineData(".abc.101.")]
    [InlineData(".104.x.")]
    [InlineData(".104(a).301.")]
    public void Top_level_or_unreadable_scopes_give_null(string? scopeName)
    {
        Assert.Null(ScopePath.Parse(scopeName));
    }
}
