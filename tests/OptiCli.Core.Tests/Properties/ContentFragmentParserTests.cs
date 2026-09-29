using OptiCli.Core.Properties;

namespace OptiCli.Core.Tests.Properties;

public class ContentFragmentParserTests
{
    /// <summary>What the CMS writes inside every fragment element.</summary>
    private const string Body = "{}";

    private const string ClassId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string First = "11111111-1111-1111-1111-111111111111";
    private const string Second = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Plain_items_in_order()
    {
        var xhtml = $"""<div data-classid="{ClassId}" data-contentgroup="" data-contentguid="{First}" data-contentname="Teaser: Summer">{Body}</div><div data-classid="{ClassId}" data-contentguid="{Second}" data-contentname="Banner &amp; more" data-isinlineblock="false">{Body}</div>""";

        var fragments = ContentFragmentParser.Parse(xhtml);

        Assert.Equal(2, fragments.Count);
        Assert.Equal(Guid.Parse(First), fragments[0].ContentGuid);
        Assert.Equal("Teaser: Summer", fragments[0].Name);
        Assert.Null(fragments[0].ContentGroup);
        Assert.Empty(fragments[0].VisitorGroups);
        Assert.Empty(fragments[0].RenderSettings);
        Assert.False(fragments[0].IsInline);
        Assert.Equal(1, fragments[1].Index);
        Assert.Equal("Banner & more", fragments[1].Name);
    }

    [Fact]
    public void Display_option_and_personalization()
    {
        var xhtml = $"""
            <div data-classid="{ClassId}" data-contentgroup="g1" data-contentguid="{First}" data-contentname="For members" data-groups="33333333-3333-3333-3333-333333333333, 44444444-4444-4444-4444-444444444444" data-epi-content-display-option="half">{Body}</div>
            <div data-classid="{ClassId}" data-contentgroup="g1" data-contentguid="{Second}" data-contentname="Fallback" data-epi-content-display-option='full'>{Body}</div>
            """;

        var fragments = ContentFragmentParser.Parse(xhtml);

        Assert.Equal("half", fragments[0].DisplayOption);
        Assert.Equal("g1", fragments[0].ContentGroup);
        Assert.Equal(["33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444"], fragments[0].VisitorGroups);
        Assert.Equal("full", fragments[1].DisplayOption);
        Assert.Equal("g1", fragments[1].ContentGroup);
        Assert.Empty(fragments[1].VisitorGroups);
    }

    [Fact]
    public void Inline_blocks_have_a_type_and_no_identity()
    {
        var xhtml = $"""<div data-classid="{ClassId}" data-contentguid="00000000-0000-0000-0000-000000000000" data-contentname="" data-id="intro" data-inlineblockname="Intro text" data-inlineblocktypeid="20">{Body}</div>""";

        var fragment = Assert.Single(ContentFragmentParser.Parse(xhtml));

        Assert.True(fragment.IsInline);
        Assert.Null(fragment.ContentGuid);
        Assert.Equal(20, fragment.InlineTypeId);
        Assert.Equal("Intro text", fragment.InlineName);
        Assert.Equal(new Dictionary<string, string> { ["id"] = "intro" }, fragment.RenderSettings);
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData("123_456", 123)]
    [InlineData("123__catalog", 123)]
    public void Older_serialisation_by_content_link(string link, int id)
    {
        var fragment = Assert.Single(ContentFragmentParser.Parse($"""<div data-contentlink="{link}" data-contentname="Old">{Body}</div>"""));

        Assert.Equal(id, fragment.ContentLink);
        Assert.False(fragment.IsInline);
    }

    [Fact]
    public void Blocks_embedded_in_rich_text_are_found_between_ordinary_markup()
    {
        var html = $"""<p>Intro <span class="note">x</span></p><div class="wide"><p>Not a block</p></div><div data-classid="{ClassId}" data-contentguid="{First}" data-contentname="Quote">{Body}</div><p>End</p>""";

        var fragment = Assert.Single(ContentFragmentParser.Parse(html));

        Assert.Equal(Guid.Parse(First), fragment.ContentGuid);
        Assert.Equal(0, fragment.Index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<p>No blocks here</p>")]
    public void Nothing_to_parse(string? xhtml)
    {
        Assert.Empty(ContentFragmentParser.Parse(xhtml));
    }
}
