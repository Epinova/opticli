using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;

namespace OptiCli.Core.Tests.Queries;

public class QueryHelpersTests
{
    [Theory]
    [InlineData("Heading=Hello", "Heading", WhereOperator.Equals, "Hello")]
    [InlineData("Heading~a=b", "Heading", WhereOperator.Contains, "a=b")]
    [InlineData("Hero.Heading=x~y", "Hero.Heading", WhereOperator.Equals, "x~y")]
    [InlineData(" Name ~ ", "Name", WhereOperator.Contains, " ")]
    public void Where_clauses_split_at_the_first_operator(string text, string property, WhereOperator op, string value)
    {
        Assert.Equal(new WhereClause(property, op, value), WhereClause.Parse(text));
    }

    [Theory]
    [InlineData("Heading")]
    [InlineData("=value")]
    public void Where_clauses_need_a_property_and_operator(string text)
    {
        Assert.Throws<UsageException>(() => WhereClause.Parse(text));
    }

    [Fact]
    public void Contains_patterns_escape_like_wildcards()
    {
        Assert.Equal(@"%50\% off\_now \[x]\\%", WhereClause.ContainsPattern(@"50% off_now [x]\"));
    }

    [Fact]
    public void Sql_windows_page_with_one_extra_row()
    {
        Assert.Equal((0, 50), Paging.Window(null, null));
        Assert.Equal((20, 10), Paging.Window(10, "20"));
        Assert.Throws<UsageException>(() => Paging.Window(0, null));
        Assert.Throws<UsageException>(() => Paging.Window(10, "x"));

        var more = Paging.FromWindow([1, 2, 3], offset: 20, limit: 2);
        Assert.Equal([1, 2], more.Items);
        Assert.Equal("22", more.Next);
        Assert.Null(Paging.FromWindow([1, 2], 20, 2).Next);
    }

    [Theory]
    [InlineData("<p>We serve <b>fresh</b> coffee every&nbsp;day.</p>", "coffee", "We serve fresh coffee every day.")]
    [InlineData("<p>no match</p>", "zzz", "no match")]
    public void Snippets_strip_markup(string raw, string text, string expected)
    {
        Assert.Equal(expected, SearchReader.Snippet(raw, text).Replace(' ', ' '));
    }

    [Fact]
    public void Snippets_are_cut_around_the_match()
    {
        var raw = new string('a', 200) + " needle " + new string('b', 200);

        var snippet = SearchReader.Snippet(raw, "needle");

        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains("needle", snippet);
        Assert.True(snippet.Length < 150);
    }

    [Fact]
    public void Children_sort_by_the_parents_rule()
    {
        static ContentHeader Child(int id, string name, int peer, DateTime created) => new(
            id, Guid.NewGuid(), 1, 99, ".1.99.", 1, false, 0, peer,
            new Dictionary<int, ContentLanguageRow> { [1] = new(1, name, name, VersionStatus.Published, 1, created, created, created, null, null, null, null, null) });
        ContentHeader[] children = [Child(1, "Beta", 2, new DateTime(2024, 1, 2)), Child(2, "alpha", 3, new DateTime(2024, 1, 3)), Child(3, "Gamma", 1, new DateTime(2024, 1, 1))];

        Assert.Equal([2, 1, 3], ChildOrder.Sort(children, ChildOrder.Alphabetical, 1).Select(c => c.Id));
        Assert.Equal([3, 1, 2], ChildOrder.Sort(children, ChildOrder.Index, 1).Select(c => c.Id));
        Assert.Equal([2, 1, 3], ChildOrder.Sort(children, ChildOrder.CreatedDescending, 1).Select(c => c.Id));
        Assert.Equal([3, 1, 2], ChildOrder.Sort(children, ChildOrder.CreatedAscending, 1).Select(c => c.Id));
    }

    [Theory]
    [InlineData(null, VersionKind.Published, null)]
    [InlineData("Published", VersionKind.Published, null)]
    [InlineData("latest", VersionKind.Latest, null)]
    [InlineData("456", VersionKind.Specific, 456)]
    public void Version_selectors(string? text, VersionKind kind, int? id)
    {
        Assert.Equal(new VersionSelector(kind, id), VersionSelector.Parse(text));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Invalid_version_selectors(string text)
    {
        Assert.Throws<UsageException>(() => VersionSelector.Parse(text));
    }
}
