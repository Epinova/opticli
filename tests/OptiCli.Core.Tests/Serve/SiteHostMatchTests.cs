using OptiCli.Core.Cms;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class SiteHostMatchTests
{
    private static SiteInfo Site(string name, string? url, params HostInfo[] hosts) =>
        new(1, Guid.NewGuid(), name, url, "5", "Home", "en", null, hosts);

    private static HostInfo Host(string name, HostType type = HostType.Undefined, bool? https = null) => new(name, type, null, https);

    [Fact]
    public void A_database_with_only_production_hosts_warns_that_every_page_is_a_404_and_says_how_to_point_a_site_here()
    {
        var sites = new[] { Site("Alloy", "https://alloy.example.com/", Host("alloy.example.com", HostType.Primary, https: true), Host("www.alloy.example.com", HostType.RedirectPermanent)) };

        var warning = SiteHostMatch.Warning(sites, 5199, null);

        Assert.NotNull(warning);
        Assert.Contains("No site of the database answers on http://127.0.0.1:5199, so every page there is a 404", warning);
        Assert.Contains("alloy.example.com (Alloy)", warning);
        // A redirect host isn't one the site answers on: not listed either.
        Assert.DoesNotContain("www.alloy.example.com", warning);
        Assert.Contains("opticli sites primary Alloy=localhost:5199", warning);
        Assert.DoesNotContain("--port", warning);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("localhost:5199")]
    [InlineData("127.0.0.1:5199")]
    [InlineData("LOCALHOST:5199")]
    public void A_site_with_the_wildcard_or_a_host_on_serves_address_answers(string host)
    {
        var sites = new[]
        {
            Site("Alloy", "https://alloy.example.com/", Host("alloy.example.com", HostType.Primary)),
            Site("Other", "https://other.example.com/", Host(host)),
        };

        Assert.Null(SiteHostMatch.Warning(sites, 5199, null));
    }

    [Fact]
    public void The_https_address_counts_and_a_local_host_on_another_port_is_named_as_the_port_to_serve_on()
    {
        var sites = new[] { Site("Alloy", "https://localhost:9299/", Host("localhost:9299", HostType.Primary, https: true), Host("localhost:5001", HostType.Edit, https: false)) };

        Assert.Null(SiteHostMatch.Warning(sites, 5199, 9299));
        var warning = SiteHostMatch.Warning(sites, 5199, null)!;
        Assert.Contains("Start serve on the port a site has (--https-port 9299 or --port 5001)", warning);
    }

    [Fact]
    public void A_redirect_host_on_serves_address_doesnt_count_but_the_site_url_and_the_default_application_do()
    {
        Assert.NotNull(SiteHostMatch.Warning([Site("Alloy", "https://alloy.example.com/", Host("localhost:5199", HostType.RedirectPermanent))], 5199, null));
        Assert.Null(SiteHostMatch.Warning([Site("Alloy", "http://localhost:5199/")], 5199, null));
        Assert.Null(SiteHostMatch.Warning([Site("Alloy", "https://alloy.example.com/", Host("alloy.example.com")) with { IsDefault = true }], 5199, null));
    }

    [Fact]
    public void A_database_without_sites_and_a_site_name_with_spaces()
    {
        // The CMS makes a site on the first request: nothing to warn about yet.
        Assert.Null(SiteHostMatch.Warning([], 5199, null));
        Assert.Contains("opticli sites primary \"Site A=localhost:5200\"", SiteHostMatch.Warning([Site("Site A", null, Host("site-a.example.com"))], 5200, null));
    }
}
