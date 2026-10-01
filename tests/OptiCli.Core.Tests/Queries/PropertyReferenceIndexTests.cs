using OptiCli.Core.Content;
using OptiCli.Core.Queries;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Queries;

public class PropertyReferenceIndexTests
{
    private static readonly Guid Block = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    private static readonly Guid Other = Guid.Parse("11111111-2222-4333-8444-555555555555");

    [Theory]
    [InlineData("<div data-contentguid=\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\"></div>", true)]
    [InlineData("<DIV DATA-CONTENTGUID=\"0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D\"></DIV>", true)]
    [InlineData("<a href=\"~/link/0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4d.aspx\">x</a>", false)]
    [InlineData("[\"x0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4dx\"]", false)]
    // An N form inside a longer run of hex digits, as LIKE '%…%' finds it.
    [InlineData("ff0a1b2c3d4e5f4a6b8c7d9e0f1a2b3c4dff", false)]
    public void Guids_are_found_in_either_form_and_case_anywhere_in_the_text(string text, bool fragment)
    {
        Assert.Contains((Block, fragment), GuidText.Find(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4")]
    [InlineData("0a1b2c3d 4e5f 4a6b 8c7d 9e0f1a2b3c4d")]
    [InlineData("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4g")]
    public void Text_without_a_whole_guid_has_none(string? text)
    {
        Assert.DoesNotContain(GuidText.Find(text), g => g.Guid == Block);
    }

    [Fact]
    public void A_value_is_found_once_by_its_link_columns_and_once_by_its_text()
    {
        var index = new PropertyReferenceIndex();
        index.Add(100, English, MainArea, null, contentLink: 42, linkGuid: Block, text: null, longText: null);
        index.Add(101, English, MainArea, null, null, null, text: Block.ToString("N"),
            longText: $"<div data-contentguid=\"{Block:D}\"></div><a href=\"~/link/{Other:N}.aspx\"></a><p>{Block:D}</p>");
        index.Add(102, Swedish, Heading, ".104.301.", null, null, null, longText: $"<a href=\"~/link/{Block:N}.aspx\"></a>");

        var hits = index.For(Header(42, Block)).ToList();

        Assert.Equal(
            [(100, true, false), (101, false, true), (102, false, false)],
            hits.Select(h => (h.Owner, h.ByLink, h.AsFragment)));
        Assert.Equal(".104.301.", hits[2].Scope);
        Assert.Equal([101], index.For(Header(43, Other)).Select(h => h.Owner));
    }

    private static ContentHeader Header(int id, Guid guid) =>
        new(id, guid, TeaserBlock, 3, ".1.3.", English, false, 0, 0, new Dictionary<int, ContentLanguageRow>());
}
