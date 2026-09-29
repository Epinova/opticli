using OptiCli.Core.Properties;

namespace OptiCli.Core.Tests.Properties;

public class LinkParsingTests
{
    private static readonly Guid Target = Guid.Parse("0b6f9c4e-3f1a-4d2b-9a8e-1c2d3e4f5a6b");

    [Theory]
    [InlineData("""<a href="~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx">x</a>""", null, null)]
    [InlineData("""<a href="/link/0b6f9c4e-3f1a-4d2b-9a8e-1c2d3e4f5a6b.aspx#contact">x</a>""", null, "contact")]
    [InlineData("""<a href="~/link/0B6F9C4E3F1A4D2B9A8E1C2D3E4F5A6B.aspx?id=1&amp;epslanguage=sv#top">x</a>""", "sv", "top")]
    [InlineData("""{"href":"~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx?epslanguage=en"}""", "en", null)]
    public void Permanent_links_in_markup_and_json(string text, string? language, string? anchor)
    {
        var link = Assert.Single(PermanentLinks.Find(text));

        Assert.Equal(Target, link.Guid);
        Assert.Equal(language, link.Language);
        Assert.Equal(anchor, link.Anchor);
    }

    [Fact]
    public void Several_links_in_rich_text()
    {
        var html = """<p><a href="~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx">One</a> and <img src="~/link/11111111111111111111111111111111.aspx" /></p>""";

        Assert.Equal([Target, Guid.Parse("11111111-1111-1111-1111-111111111111")], PermanentLinks.Find(html).Select(l => l.Guid));
    }

    [Theory]
    [InlineData("~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx", true)]
    [InlineData(" ~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx?epslanguage=en ", true)]
    [InlineData("https://www.example.com/", false)]
    [InlineData("see ~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx", false)]
    public void Single_is_only_a_whole_value_link(string value, bool expected)
    {
        Assert.Equal(expected ? Target : null, PermanentLinks.Single(value));
    }

    [Fact]
    public void Link_item()
    {
        var link = Assert.Single(LinkMarkup.Parse("""<a class="cta" title="Read more" target="_blank" href="~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx">Read <b>more</b> &amp; learn</a>"""));

        Assert.Equal("~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx", link.Href);
        Assert.Equal("Read more & learn", link.Text);
        Assert.Equal("Read more", link.Title);
        Assert.Equal("_blank", link.Target);
        Assert.Equal(new Dictionary<string, string> { ["class"] = "cta" }, link.Attributes);
    }

    [Fact]
    public void Link_collection()
    {
        var links = LinkMarkup.Parse("""<links><a href="https://www.example.com/">Example</a><a title="Contact" href="mailto:info@example.com">Mail us</a><a href="/about/" /></links>""");

        Assert.Equal(["https://www.example.com/", "mailto:info@example.com", "/about/"], links.Select(l => l.Href));
        Assert.Equal(["Example", "Mail us", null], links.Select(l => l.Text));
    }
}
