using System.Text.Json;
using OptiCli.Core.Content;
using OptiCli.Core.Tests.Content;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class SimpleAddressCheckTests
{
    private static PropertyChange Change(string property, string? after) =>
        new(property, null, after is null ? null : JsonSerializer.SerializeToElement(after));

    [Theory]
    [InlineData("/campaign", "~/campaign")]
    [InlineData("campaign/2025/", "~/campaign/2025")]
    public void The_new_address_is_compared_as_stored(string after, string stored) =>
        Assert.Equal(stored, SimpleAddressCheck.Stored([Change("Heading", "x"), Change("SimpleAddress", after)]));

    [Fact]
    public void Clearing_or_not_changing_the_address_checks_nothing()
    {
        Assert.Null(SimpleAddressCheck.Stored([Change("SimpleAddress", null)]));
        Assert.Null(SimpleAddressCheck.Stored([Change("Heading", "/campaign")]));
    }

    [Fact]
    public void The_site_is_the_one_whose_start_page_is_on_the_path()
    {
        var sites = ModelFixture.Create().Sites;

        Assert.Equal("Example", SimpleAddressCheck.SiteOf(sites, [1, 5, 42])?.Name);
        Assert.Equal("Example", SimpleAddressCheck.SiteOf(sites, [1, 5])?.Name);
        Assert.Null(SimpleAddressCheck.SiteOf(sites, [1, 3, 42]));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("~/", null)]
    [InlineData("~/campaign", "/campaign")]
    public void Get_shows_simple_addresses_as_paths(string? stored, string? shown) =>
        Assert.Equal(shown, ShortcutInfo.SimpleAddressPath(stored));

    [Theory]
    [InlineData("target=\"_blank\"", "_blank")]
    [InlineData("_top", "_top")]
    [InlineData(null, null)]
    public void Windows_are_named_as_set_takes_them(string? stored, string? shown) =>
        Assert.Equal(shown, ShortcutInfo.FrameTarget(stored));

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(1, "shortcut")]
    [InlineData(2, "external")]
    [InlineData(3, "inactive")]
    [InlineData(4, "fetchData")]
    [InlineData(7, null)]
    public void Link_types_are_named_as_set_takes_them(int? linkType, string? name) =>
        Assert.Equal(name, ShortcutInfo.TypeName(linkType));
}
