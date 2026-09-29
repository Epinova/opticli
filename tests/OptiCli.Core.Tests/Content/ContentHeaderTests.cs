using OptiCli.Core.Content;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Content;

public class ContentHeaderTests
{
    private static readonly Guid TargetGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static ContentLanguageRow Row(int language, string segment, VersionStatus status, int version, LinkTarget? link = null) =>
        new(language, segment, segment, status, version, null, null, null, null, null, null, null, null, link);

    private static ContentHeader Header(params ContentLanguageRow[] rows) =>
        new(123, Guid.NewGuid(), ArticlePage, 5, ".1.5.", English, false, 0, 0, rows.ToDictionary(r => r.LanguageId));

    [Fact]
    public void Urls_use_a_branch_segment_only_once_that_branch_is_published()
    {
        var header = Header(Row(English, "privacy", VersionStatus.Published, 10), Row(Swedish, "integritet", VersionStatus.CheckedIn, 11));

        Assert.Equal("privacy", header.RoutingRow(Swedish)!.UrlSegment);
        Assert.Equal("privacy", header.RoutingRow(English)!.UrlSegment);
        Assert.Equal("integritet", Header(Row(English, "privacy", VersionStatus.Published, 10), Row(Swedish, "integritet", VersionStatus.Published, 12))
            .RoutingRow(Swedish)!.UrlSegment);
        // Nothing published at all: the branch's own segment.
        Assert.Equal("integritet", Header(Row(English, "privacy", VersionStatus.CheckedOut, 10), Row(Swedish, "integritet", VersionStatus.CheckedIn, 11))
            .RoutingRow(Swedish)!.UrlSegment);
    }

    [Fact]
    public void The_primary_version_of_a_never_published_branch_is_its_common_draft()
    {
        // tblContentLanguage.Version can still name the first version saved while the primary values are the common draft's.
        Assert.Equal(20, VersionStatuses.PrimaryVersion(VersionStatus.CheckedIn, storedVersion: 10, commonDraft: 20));
        Assert.Equal(10, VersionStatuses.PrimaryVersion(VersionStatus.CheckedIn, storedVersion: 10, commonDraft: null));
        Assert.Equal(10, VersionStatuses.PrimaryVersion(VersionStatus.Published, storedVersion: 10, commonDraft: 20));
    }

    [Theory]
    [InlineData(true, true, null, true, null)]
    [InlineData(null, true, null, true, null)]
    [InlineData(true, false, null, false, null)]
    [InlineData(false, false, "~/link/44444444444444444444444444444444.aspx#contact", true, "contact")]
    [InlineData(false, false, "https://www.example.com/", false, null)]
    [InlineData(false, false, null, false, null)]
    public void Link_targets_come_from_shortcuts_and_links_to_content(bool? automaticLink, bool shortcut, string? linkUrl, bool expected, string? anchor)
    {
        var link = LinkTarget.From(automaticLink, shortcut ? TargetGuid : null, linkUrl);

        Assert.Equal(expected, link is not null);
        if (link is not null)
        {
            Assert.Equal(TargetGuid, link.Guid);
            Assert.Equal(anchor, link.Anchor);
        }
    }
}
