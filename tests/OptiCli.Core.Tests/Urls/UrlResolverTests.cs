using OptiCli.Core.Cms;
using OptiCli.Core.Urls;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Urls;

public class UrlResolverTests
{
    private static readonly SiteInfo Other = Site(new HostInfo("other.example.org", HostType.Primary, "en", true)) with { Id = 2, Name = "Other", StartPage = "7" };

    private static readonly SiteMap Sites = Create([Site(), Other]).Sites;

    [Fact]
    public void A_simple_address_leads_to_the_page_of_the_site_that_owns_the_host()
    {
        // Both sites use the same address; the other site's page has the lower id.
        UrlResolver.SimpleAddressRow[] rows = [new(40, English, [1, 7, 40]), new(50, English, [1, 5, 30, 50])];

        Assert.Equal(50, UrlResolver.BestSimpleAddress(Sites, rows, Sites.RequireSite("Example"), En)?.ContentId);
        Assert.Equal(40, UrlResolver.BestSimpleAddress(Sites, rows, Other, En)?.ContentId);
    }

    [Fact]
    public void Pages_outside_every_site_count_for_any_site_and_the_requested_language_goes_first()
    {
        UrlResolver.SimpleAddressRow[] rows = [new(40, English, [1, 7, 40]), new(60, English, [1, 2, 60]), new(70, Swedish, [1, 5, 70])];
        var example = Sites.RequireSite("Example");

        Assert.Equal(70, UrlResolver.BestSimpleAddress(Sites, rows, example, Sv)?.ContentId);
        Assert.Equal(60, UrlResolver.BestSimpleAddress(Sites, rows, example, En)?.ContentId);
        Assert.Null(UrlResolver.BestSimpleAddress(Sites, rows[..1], example, En));
    }
}
