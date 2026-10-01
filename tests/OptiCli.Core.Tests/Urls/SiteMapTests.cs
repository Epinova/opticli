using OptiCli.Core.Cms;
using OptiCli.Core.Errors;
using OptiCli.Core.Urls;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Urls;

public class SiteMapTests
{
    private static SiteMap Map(params SiteInfo[] sites) => Create(sites.Length > 0 ? sites : null).Sites;

    [Fact]
    public void Host_picks_the_site_and_its_language()
    {
        var parsed = Map().Parse("https://www.example.se/om-oss/team/?page=2#top", null);

        Assert.Equal("Example", parsed.Site!.Name);
        Assert.Equal("www.example.se", parsed.Host);
        Assert.Equal(UrlRoot.StartPage, parsed.Root);
        Assert.Equal(5, parsed.RootId);
        Assert.Equal("sv", parsed.Language!.Code);
        Assert.Equal("host", parsed.LanguageSource);
        Assert.Equal(["om-oss", "team"], parsed.Segments);
    }

    [Fact]
    public void A_language_prefix_wins_over_the_host_language()
    {
        var parsed = Map().Parse("https://www.example.com/se/om-oss/", null);

        Assert.Equal("sv", parsed.Language!.Code);
        Assert.Equal("path", parsed.LanguageSource);
        Assert.Equal(["om-oss"], parsed.Segments);
    }

    [Fact]
    public void A_bare_path_uses_the_only_site_and_its_primary_host_language()
    {
        var parsed = Map().Parse("/about/team%20a/", null);

        Assert.Equal("en", parsed.Language!.Code);
        Assert.Equal("site", parsed.LanguageSource);
        Assert.Equal(["about", "team a"], parsed.Segments);
    }

    [Fact]
    public void Asset_paths_start_at_the_asset_roots_without_a_language()
    {
        var map = Map();

        var global = map.Parse("/globalassets/logos/logo.png", null);
        Assert.Equal((UrlRoot.GlobalAssets, 3), (global.Root, global.RootId));
        Assert.Null(global.Language);
        Assert.Equal(["logos", "logo.png"], global.Segments);

        Assert.Equal((UrlRoot.SiteAssets, 6), (map.Parse("/siteassets/a.pdf", null).Root, map.Parse("/siteassets/a.pdf", null).RootId));
        Assert.Equal(UrlRoot.ContentAssets, map.Parse("/contentassets/0123abcd/a.pdf", null).Root);
    }

    [Fact]
    public void Several_sites_without_a_wildcard_host_need_site_for_a_path()
    {
        var other = Site(new HostInfo("other.example.org", HostType.Primary, "en", true)) with { Id = 2, Name = "Other", StartPage = "7" };
        var map = Map(Site(), other);

        Assert.Throws<UsageException>(() => map.Parse("/about/", null));
        Assert.Equal(7, map.Parse("/about/", map.RequireSite("other.example.org")).RootId);
        Assert.Equal(7, map.Parse("https://other.example.org/about/", null).RootId);
        Assert.Equal("Did you mean Other?", Assert.Throws<NotFoundException>(() => map.RequireSite("Othr")).Hint);
    }

    [Fact]
    public void The_wildcard_site_answers_unknown_hosts_and_bare_paths()
    {
        var wildcard = Site(new HostInfo("*", HostType.Undefined, "sv", null), new HostInfo("site.example", HostType.Primary, null, true));
        var other = Site(new HostInfo("other.example.org", HostType.Primary, "en", true)) with { Id = 2, Name = "Other", StartPage = "7" };
        var map = Map(other, wildcard);

        Assert.Equal(5, map.Parse("/about/", null).RootId);
        var unknown = map.Parse("http://localhost:5000/about/", null);
        Assert.Equal(5, unknown.RootId);
        Assert.Equal("sv", unknown.Language!.Code);
    }

    [Fact]
    public void Unknown_host_without_a_wildcard_is_not_found()
    {
        Assert.Throws<NotFoundException>(() => Map().Parse("https://unknown.example.net/", null));
    }

    [Theory]
    [InlineData("about")]
    [InlineData("ftp://example.com/")]
    public void Not_a_url(string url)
    {
        Assert.Throws<UsageException>(() => Map().Parse(url, null));
    }

    [Fact]
    public void Page_urls_leave_out_the_prefix_for_a_language_mapped_to_a_host()
    {
        var map = Map();
        string? Segment(int id) => id switch { 5 => "home", 100 => "about", 101 => "team", _ => null };
        int[] path = [1, 5, 100, 101];

        var english = map.Compose(path, Segment, En, ContentKind.Page)!;
        Assert.Equal("/about/team/", english.Path);
        Assert.Equal("https://www.example.com/about/team/", english.Absolute);

        var swedish = map.Compose(path, Segment, Sv, ContentKind.Page)!;
        Assert.Equal("/about/team/", swedish.Path);
        Assert.Equal("https://www.example.se/about/team/", swedish.Absolute);

        Assert.Equal("/", map.Compose([1, 5], Segment, En, ContentKind.Page)!.Path);
    }

    [Fact]
    public void Page_urls_get_a_language_prefix_when_no_host_is_mapped()
    {
        var map = Map(Site(new HostInfo("www.example.com", HostType.Primary, "en", true)));

        var url = map.Compose([1, 5, 100], id => id == 100 ? "om-oss" : "home", Sv, ContentKind.Page)!;

        Assert.Equal("/se/om-oss/", url.Path);
        Assert.Equal("https://www.example.com/se/om-oss/", url.Absolute);
    }

    [Fact]
    public void Media_urls_follow_the_asset_root()
    {
        var map = Map();
        string? Segment(int id) => id switch { 3 => "SysGlobalAssets", 200 => "logos", 201 => "logo.png", 6 => "SysSiteAssets", 4 => "SysContentAssets", 300 => "0123abcd", _ => null };

        Assert.Equal("/globalassets/logos/logo.png", map.Compose([1, 3, 200, 201], Segment, null, ContentKind.Media)!.Path);
        Assert.Equal("/siteassets/logo.png", map.Compose([1, 5, 6, 201], Segment, null, ContentKind.Media)!.Path);
        Assert.Equal("/contentassets/0123abcd/logo.png", map.Compose([1, 4, 300, 201], Segment, null, ContentKind.Media)!.Path);
    }

    [Fact]
    public void Urls_end_in_a_slash_unless_the_last_segment_has_an_extension()
    {
        var map = Map();
        string? Segment(int id) => id switch
        {
            5 => "home", 100 => "robots.txt", 101 => ".well-known", 102 => "report-for-q2.-2015", 103 => "brochure",
            4 => "SysContentAssets", 300 => "0123abcd", _ => null,
        };

        Assert.Equal("/robots.txt", map.Compose([1, 5, 100], Segment, En, ContentKind.Page)!.Path);
        Assert.Equal("/.well-known", map.Compose([1, 5, 101], Segment, En, ContentKind.Page)!.Path);
        Assert.Equal("/report-for-q2.-2015", map.Compose([1, 5, 102], Segment, En, ContentKind.Page)!.Path);
        Assert.Equal("/brochure/", map.Compose([1, 5, 103], Segment, En, ContentKind.Page)!.Path);
        // A media file uploaded without an extension, and the folder it is in.
        Assert.Equal("/contentassets/0123abcd/brochure/", map.Compose([1, 4, 300, 103], Segment, null, ContentKind.Media)!.Path);
        Assert.Equal("/contentassets/0123abcd/", map.Compose([1, 4, 300], Segment, null, ContentKind.Folder)!.Path);
    }

    [Fact]
    public void No_url_outside_the_site_or_with_a_missing_segment()
    {
        var map = Map();

        Assert.Null(map.Compose([1, 2, 100], _ => "x", En, ContentKind.Page));
        Assert.Null(map.Compose([1, 5, 100], id => id == 5 ? "home" : null, En, ContentKind.Page));
    }

    [Fact]
    public void A_site_whose_start_page_is_below_another_sites_owns_that_part_of_the_tree()
    {
        // The outer site comes first in id order; the inner site's start page (100) is one of its pages.
        var inner = Site(new HostInfo("inner.example.org", HostType.Primary, "en", true)) with { Id = 2, Name = "Inner", StartPage = "100" };
        var map = Map(Site(), inner);
        string Segment(int id) => id switch { 100 => "campaign", 101 => "offer", _ => "home" };

        var url = map.Compose([1, 5, 100, 101], Segment, En, ContentKind.Page)!;

        Assert.Equal(("/offer/", "https://inner.example.org/offer/", "Inner"), (url.Path, url.Absolute, url.Site));
        Assert.Equal("Inner", map.SiteOf([1, 5, 100])?.Name);
        Assert.Equal("Example", map.SiteOf([1, 5, 102])?.Name);
        Assert.Equal("/", map.Compose([1, 5, 100], Segment, En, ContentKind.Page)!.Path);
    }

    [Fact]
    public void Round_trip_between_compose_and_parse()
    {
        var map = Map(Site(new HostInfo("www.example.com", HostType.Primary, "en", true)));

        var url = map.Compose([1, 5, 100, 101], id => id switch { 100 => "om-oss", 101 => "team", _ => "home" }, Sv, ContentKind.Page)!;
        var parsed = map.Parse(url.Absolute!, null);

        Assert.Equal("sv", parsed.Language!.Code);
        Assert.Equal(["om-oss", "team"], parsed.Segments);
    }
}
