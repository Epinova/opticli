using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

public class RichTextMarkupTests
{
    [Theory]
    // As the CMS stored what a plan gave (seen on CMS 13.3).
    [InlineData("<p><img src=\"~/link/e2c7.aspx\" alt=\"x\"></p>", "<p><img src=\"~/link/e2c7.aspx\" alt=\"x\" /></p>")]
    [InlineData("<p>a<br>b<BR>c</p><hr>", "<p>a<br />b<br />c</p><hr />")]
    [InlineData("<img src=\"/globalassets/x.png\" alt='y'>", "<img src=\"/globalassets/x.png\" alt=\"y\" />")]
    [InlineData("<a href=\"/en/\" class=c>t</a>", "<a href=\"/en/\" class=\"c\">t</a>")]
    [InlineData("<input type=checkbox checked>", "<input type=\"checkbox\" checked />")]
    [InlineData("<P CLASS=\"lead\">x</P >", "<p class=\"lead\">x</p>")]
    [InlineData("<span title='say \"hi\"'>x</span>", "<span title=\"say &quot;hi&quot;\">x</span>")]
    [InlineData("<br/>", "<br />")]
    [InlineData("<div\n  class=\"a\"\n>x</div>", "<div class=\"a\">x</div>")]
    public void Tags_written_another_way_are_the_same(string given, string stored)
    {
        Assert.True(RichTextMarkup.Same(given, stored));
        Assert.Equal(stored, RichTextMarkup.Normalize(given));
    }

    [Theory]
    [InlineData("<p>Hello</p>", "<p>Hello!</p>")]
    [InlineData("<p class=\"a\">x</p>", "<p class=\"A\">x</p>")]
    [InlineData("<p>&nbsp;</p>", "<p>&#160;</p>")]
    [InlineData("<img alt=\"x\" src=\"a.png\">", "<img src=\"a.png\" alt=\"x\" />")]
    [InlineData("<input checked>", "<input checked=\"\" />")]
    [InlineData("<p>a</p>", "<p>a</p> ")]
    [InlineData("<!-- <br> --><p>a</p>", "<!-- <br /> --><p>a</p>")]
    public void Other_differences_count(string given, string stored) =>
        Assert.False(RichTextMarkup.Same(given, stored));

    [Fact]
    public void Text_outside_tags_stays_as_it_is() =>
        Assert.Equal("a < b && c > d", RichTextMarkup.Normalize("a < b && c > d"));
}
