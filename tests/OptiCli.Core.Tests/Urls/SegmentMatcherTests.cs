using OptiCli.Core.Urls;

namespace OptiCli.Core.Tests.Urls;

public class SegmentMatcherTests
{
    private const int En = 1;
    private const int Sv = 2;

    private static readonly ChildSegment[] Children =
    [
        new(10, En, "about"),
        new(10, Sv, "om-oss"),
        new(11, En, "news"),
        new(12, En, "contact"),
        new(13, Sv, "kontakt"),
    ];

    [Theory]
    [InlineData("om-oss", Sv, 10)]
    [InlineData("OM-OSS", Sv, 10)]
    [InlineData("about", En, 10)]
    [InlineData("contact", Sv, 12)]
    [InlineData("kontakt", En, 13)]
    public void Matches_in_the_language_or_falls_back_for_children_without_that_branch(string segment, int language, int expected)
    {
        Assert.Equal(expected, SegmentMatcher.Match(Children, segment, language)?.ContentId);
    }

    [Fact]
    public void A_child_that_has_the_language_does_not_match_by_another_languages_segment()
    {
        Assert.Null(SegmentMatcher.Match(Children, "about", Sv));
        Assert.Null(SegmentMatcher.Match(Children, "missing", En));
    }

    [Fact]
    public void Without_a_language_any_branch_matches()
    {
        Assert.Equal(10, SegmentMatcher.Match(Children, "om-oss", null)?.ContentId);
    }

    [Fact]
    public void A_never_published_branch_is_routed_by_the_master_segment_but_still_matches_its_own()
    {
        ChildSegment[] children = [new(20, En, "privacy"), new(20, Sv, "integritet", Published: false)];

        Assert.Equal(20, SegmentMatcher.Match(children, "privacy", Sv)?.ContentId);
        Assert.Equal(20, SegmentMatcher.Match(children, "integritet", Sv)?.ContentId);
    }
}
