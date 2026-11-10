using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

public class PublicUrlsTests
{
    [Theory]
    [InlineData("https://www.example.com:9377/en/about/", "/en/about/")]
    [InlineData("http://127.0.0.1:5199/en/", "/en/")]
    [InlineData("HTTPS://www.example.com/en/?q=1#top", "/en/?q=1#top")]
    [InlineData("https://www.example.com", "/")]
    [InlineData("https://www.example.com?q=1", "/?q=1")]
    [InlineData("//www.example.com/globalassets/teaser.png", "/globalassets/teaser.png")]
    public void Drops_the_scheme_and_host(string url, string path) =>
        Assert.Equal(path, PublicUrls.HostRelative(url));

    [Theory]
    [InlineData("/en/about/")]
    [InlineData("/")]
    [InlineData("/en/search/?q=https://www.example.com/")]
    public void Leaves_a_host_relative_url_as_it_is(string url) =>
        Assert.Equal(url, PublicUrls.HostRelative(url));
}
