using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp.Tests;

/// <summary>Which versions get a preview link, and where a relative link is made absolute.</summary>
public sealed class ResultUrlsTests
{
    [Theory]
    [InlineData("123_456", "checkedOut", true)]
    [InlineData("123_456", "awaitingApproval", true)]
    [InlineData("123_456", "delayedPublish", true)]
    [InlineData("123_456", "previouslyPublished", true)]
    [InlineData("123_456", null, true)]
    // What visitors see already has its public URL.
    [InlineData("123_456", "published", false)]
    // Content, not a version: nothing to preview that url doesn't show.
    [InlineData("123", "checkedOut", false)]
    [InlineData(null, "checkedOut", false)]
    [InlineData("not a ref", "checkedOut", false)]
    public void A_saved_version_that_isnt_published_gets_a_preview_link(string? reference, string? status, bool previews) =>
        Assert.Equal(previews, ResultUrls.Previews(reference, status));

    [Theory]
    [InlineData("/EPiServer/CMS/Content/en/about-us,,10_3191/?epieditmode=false", "https://edit.example.com", "https://edit.example.com/EPiServer/CMS/Content/en/about-us,,10_3191/?epieditmode=false")]
    [InlineData("EPiServer/CMS/Content/,,37_12/", "http://127.0.0.1:5180", "http://127.0.0.1:5180/EPiServer/CMS/Content/,,37_12/")]
    // Content on another site: on that site's host, as the CMS gave it.
    [InlineData("https://other.example.com/EPiServer/CMS/Content/en/a,,5_6/", "https://edit.example.com", "https://other.example.com/EPiServer/CMS/Content/en/a,,5_6/")]
    [InlineData("", "https://edit.example.com", null)]
    [InlineData(null, "https://edit.example.com", null)]
    public void A_relative_link_is_put_on_the_given_origin(string? url, string origin, string? expected) =>
        Assert.Equal(expected, ResultUrls.Absolute(url, origin));

    [Theory]
    [InlineData("https://edit.example.com/EPiServer/CMS/?language=en#context=epi.cms.contentdata:///10_3191", "https://edit.example.com")]
    [InlineData("http://127.0.0.1:5180/EPiServer/CMS/", "http://127.0.0.1:5180")]
    [InlineData("/EPiServer/CMS/", null)]
    [InlineData(null, null)]
    public void The_preview_goes_on_the_edit_links_host(string? editUrl, string? origin) =>
        Assert.Equal(origin, ResultUrls.Origin(editUrl));
}
