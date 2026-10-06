using OptiCli.Core.Cms;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Cms;

/// <summary>What <c>types remove|remove-property|prune</c> check before the site is asked.</summary>
public class OrphanRemoverTests
{
    [Fact]
    public void A_shared_database_is_refused_before_the_site_is_asked()
    {
        var error = Assert.Throws<RefusedException>(() => OrphanRemover.RequireLocal(sharedDatabase: true));

        Assert.Equal((OrphanRemoval.SharedRefusal, OrphanRemoval.SharedHint), (error.Message, error.Hint));
        OrphanRemover.RequireLocal(sharedDatabase: false);
    }

    [Fact]
    public void Properties_are_named_once_each_on_their_type()
    {
        var references = OrphanRemover.Properties(" ArticlePage ", ["OldIntro", "oldintro", " Teaser ", ""]);

        Assert.Equal([new OrphanPropertyRef("ArticlePage", "OldIntro"), new OrphanPropertyRef("ArticlePage", "Teaser")], references);
    }

    [Theory]
    [InlineData("", "OldIntro")]
    [InlineData("ArticlePage", " ")]
    public void A_missing_type_or_property_is_a_usage_error(string type, string property)
    {
        Assert.Throws<UsageException>(() => OrphanRemover.Properties(type, [property]));
    }

    [Fact]
    public void An_agent_from_before_the_route_is_recognised_both_ways_it_answers()
    {
        // No route at all, or (0.13) types/{name} with the name "remove", which only reads with GET.
        Assert.True(OrphanRemover.IsOlderAgent(new NotFoundException("No agent route for POST /opticli/v1/types/remove. This agent speaks opticli protocol v1.")));
        Assert.True(OrphanRemover.IsOlderAgent(new UsageException("/opticli/v1/types/remove does not accept POST; use GET.")));
        Assert.False(OrphanRemover.IsOlderAgent(new NotFoundException("Nothing was removed. Nope: No content type is named 'Nope'.")));
    }

    [Fact]
    public void Value_counts_follow_a_block_property_into_the_values_stored_inside_it()
    {
        var plain = OrphanRemoval.PropertyValuesSql(block: false);
        var block = OrphanRemoval.PropertyValuesSql(block: true);

        Assert.DoesNotContain("ScopeName LIKE", plain, StringComparison.Ordinal);
        Assert.Contains("ScopeName LIKE @inner OR ScopeName LIKE @list", block, StringComparison.Ordinal);
        Assert.All(new[] { plain, block }, sql => Assert.Contains("CategoryType = @id", sql, StringComparison.Ordinal));
    }

    [Fact]
    public void What_keeps_a_type_is_described_with_its_counts()
    {
        var text = OrphanRemoval.Describe(new TypeUsage(1, 2, 0, 3, ["ArticlePage.Teaser"]));

        Assert.Equal("1 content item; 2 content items in the recycle bin; 3 versions whose page-type property names it; the block property ArticlePage.Teaser", text);
    }
}
