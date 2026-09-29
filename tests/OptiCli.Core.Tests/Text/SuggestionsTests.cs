using OptiCli.Core.Text;

namespace OptiCli.Core.Tests.Text;

public class SuggestionsTests
{
    private static readonly string[] TypeNames = ["ArticlePage", "ArticleListPage", "StartPage", "TeaserBlock", "ImageFile", "ContainerPage"];

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("same", "same", 0)]
    [InlineData("tpyes", "types", 1)]
    [InlineData("ab", "ba", 1)]
    public void Edit_distance_counts_adjacent_swaps_as_one(string a, string b, int expected)
    {
        Assert.Equal(expected, Suggestions.Levenshtein(a, b));
    }

    [Theory]
    [InlineData("ArticlPage", "ArticlePage")]
    [InlineData("articlepage", "ArticlePage")]
    [InlineData("TeaserBlok", "TeaserBlock")]
    [InlineData("Teaser", "TeaserBlock")]
    [InlineData("StartPgae", "StartPage")]
    public void Finds_the_intended_name_first(string input, string expected)
    {
        Assert.Equal(expected, Suggestions.Closest(input, TypeNames)[0]);
    }

    [Fact]
    public void Returns_nothing_when_nothing_is_close()
    {
        Assert.Empty(Suggestions.Closest("Zebra", TypeNames));
        Assert.Null(Suggestions.DidYouMean("Zebra", TypeNames));
    }

    [Fact]
    public void Limits_and_formats_suggestions()
    {
        var hint = Suggestions.DidYouMean("Page", TypeNames);

        Assert.NotNull(hint);
        Assert.StartsWith("Did you mean ", hint);
        Assert.Equal(3, Suggestions.Closest("Page", TypeNames).Count);
    }
}
