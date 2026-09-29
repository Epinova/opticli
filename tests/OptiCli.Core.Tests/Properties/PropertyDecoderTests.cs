using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Properties;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Properties;

public class PropertyDecoderTests
{
    /// <summary>What the CMS writes inside every fragment element.</summary>
    private const string Body = "{}";

    private static readonly Guid TeaserGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PageGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UnknownGuid = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class FakeLookup : IReferenceLookup
    {
        public Dictionary<Guid, JsonObject> Expansions { get; } = [];

        public ContentIdentity Content(int id) => id == 456 ? Page : ContentIdentity.MissingId(id);

        public ContentIdentity Content(Guid guid) =>
            guid == TeaserGuid ? Teaser : guid == PageGuid ? Page : ContentIdentity.MissingGuid(guid);

        public JsonObject? Expanded(Guid guid) => Expansions.GetValueOrDefault(guid);

        private static ContentIdentity Teaser => new("789", TeaserGuid, "TeaserBlock", "Summer teaser", "en", "published", null);

        private static ContentIdentity Page => new("456", PageGuid, "ArticlePage", "About us", "en", "published", "/en/about/");
    }

    private static JsonObject Decode(IEnumerable<PropertyRow> rows, DecodeOptions? options = null, FakeLookup? lookup = null) =>
        new PropertyDecoder(Create(), lookup ?? new FakeLookup(), options ?? new DecodeOptions(), English, id => id == English ? "en" : id == Swedish ? "sv" : null)
            .Decode(ArticlePage, PropertyTree.Build(rows));

    [Fact]
    public void Scalars_are_typed_and_marked_with_their_culture()
    {
        var properties = Decode(
        [
            new PropertyRow(123, Heading, Swedish, String: "Rubrik"),
            new PropertyRow(123, Priority, English, Number: 3),
            new PropertyRow(123, ShowDate, English, Boolean: true),
        ]);

        Assert.Equal("""{"type":"String","value":"Rubrik","culture":"sv"}""", properties["Heading"]!.ToJsonString());
        Assert.Equal("""{"type":"Number","value":3,"culture":"en"}""", properties["Priority"]!.ToJsonString());
        Assert.Equal("""{"type":"Boolean","value":true,"culture":"en"}""", properties["ShowDate"]!.ToJsonString());
    }

    [Fact]
    public void Content_area_items_resolve_to_identities_with_display_option_personalization_and_inline_blocks()
    {
        var xhtml = $"""
            <div data-contentguid="{TeaserGuid}" data-contentname="Old name" data-epi-content-display-option="half">{Body}</div>
            <div data-contentgroup="members" data-groups="44444444-4444-4444-4444-444444444444" data-contentguid="{UnknownGuid}" data-contentname="Gone">{Body}</div>
            <div data-contentguid="00000000-0000-0000-0000-000000000000" data-inlineblockname="Inline teaser" data-inlineblocktypeid="{TeaserBlock}">{Body}</div>
            """;
        var properties = Decode(
        [
            new PropertyRow(123, MainArea, English, LongString: xhtml),
            new PropertyRow(123, TeaserText, English, $".{MainArea}:{TeaserBlock}(2).{TeaserText}.", LongString: "<p>Inline text</p>"),
        ]);

        var items = properties["MainArea"]!["value"]!.AsArray();
        Assert.Equal(3, items.Count);
        Assert.Equal("789", items[0]!["ref"]!.GetValue<string>());
        Assert.Equal("Summer teaser", items[0]!["name"]!.GetValue<string>());
        Assert.Equal("half", items[0]!["displayOption"]!.GetValue<string>());
        Assert.True(items[1]!["missing"]!.GetValue<bool>());
        Assert.Equal("Gone", items[1]!["name"]!.GetValue<string>());
        Assert.Equal("""{"group":"members","visitorGroups":["44444444-4444-4444-4444-444444444444"]}""", items[1]!["personalization"]!.ToJsonString());
        Assert.True(items[2]!["inline"]!.GetValue<bool>());
        Assert.Equal("TeaserBlock", items[2]!["type"]!.GetValue<string>());
        Assert.Equal("<p>Inline text</p>", items[2]!["properties"]!["Text"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Rich_text_lists_resolved_links_and_embedded_blocks()
    {
        var html = $"""<p>See <a href="~/link/{PageGuid:N}.aspx#team">us</a> and <a href="~/link/{PageGuid:N}.aspx#team">again</a>.</p><div data-contentguid="{TeaserGuid}" data-contentname="Teaser">{Body}</div>""";

        var body = Decode([new PropertyRow(123, MainBody, English, LongString: html)])["MainBody"]!;

        Assert.Equal(html, body["value"]!.GetValue<string>());
        var link = Assert.Single(body["links"]!.AsArray())!;
        Assert.Equal("456", link["ref"]!.GetValue<string>());
        Assert.Equal("/en/about/", link["url"]!.GetValue<string>());
        Assert.Equal("team", link["anchor"]!.GetValue<string>());
        Assert.Equal("789", Assert.Single(body["blocks"]!.AsArray())!["ref"]!.GetValue<string>());
    }

    [Fact]
    public void Local_blocks_and_block_lists_nest()
    {
        var properties = Decode(
        [
            new PropertyRow(123, HeroHeading, English, $".{Hero}.{HeroHeading}.", String: "Welcome"),
            new PropertyRow(123, FactLabel, English, $".{Facts}(0).{FactLabel}.", String: "Founded"),
            new PropertyRow(123, FactLabel, English, $".{Facts}(1).{FactLabel}.", String: "Staff"),
        ]);

        Assert.Equal("""{"type":"Block","blockType":"HeroBlock","value":{"Heading":{"type":"String","value":"Welcome"}},"culture":"en"}""", properties["Hero"]!.ToJsonString());
        Assert.Equal("BlockList", properties["Facts"]!["type"]!.GetValue<string>());
        Assert.Equal(["Founded", "Staff"], properties["Facts"]!["value"]!.AsArray().Select(i => i!["Label"]!["value"]!.GetValue<string>()));
    }

    [Fact]
    public void References_links_and_lists_resolve()
    {
        var properties = Decode(
        [
            new PropertyRow(123, RelatedPage, English, ContentLink: 456),
            new PropertyRow(123, MoreLink, English, LongString: $"""<a href="~/link/{PageGuid:N}.aspx" title="More">Read more</a>"""),
            new PropertyRow(123, Links, English, LongString: $"""<links><a href="https://www.example.com/">Example</a><a href="~/link/{PageGuid:N}.aspx">About</a></links>"""),
            new PropertyRow(123, RelatedItems, English, LongString: $"""["{TeaserGuid}","{UnknownGuid}"]"""),
            new PropertyRow(123, Tags, English, LongString: """["news","events"]"""),
        ]);

        Assert.Equal("About us", properties["RelatedPage"]!["value"]!["name"]!.GetValue<string>());
        Assert.Equal("456", properties["MoreLink"]!["value"]!["content"]!["ref"]!.GetValue<string>());
        Assert.Equal("Read more", properties["MoreLink"]!["value"]!["text"]!.GetValue<string>());
        var links = properties["Links"]!["value"]!.AsArray();
        Assert.Null(links[0]!["content"]);
        Assert.Equal("456", links[1]!["content"]!["ref"]!.GetValue<string>());
        var related = properties["RelatedItems"]!["value"]!.AsArray();
        Assert.Equal("789", related[0]!["ref"]!.GetValue<string>());
        Assert.True(related[1]!["missing"]!.GetValue<bool>());
        Assert.Equal("""["news","events"]""", properties["Tags"]!["value"]!.ToJsonString());
    }

    [Fact]
    public void Long_strings_are_cut_unless_full_or_named_in_fields()
    {
        var text = new string('a', 1000);
        PropertyRow[] rows = [new(123, MainBody, English, LongString: text), new(123, Heading, English, String: "Short")];

        var cut = Decode(rows)["MainBody"]!;
        Assert.Equal(TextValues.MaxLength, cut["value"]!.GetValue<string>().Length);
        Assert.True(cut["truncated"]!.GetValue<bool>());
        Assert.Equal(1000, cut["length"]!.GetValue<int>());

        Assert.Equal(1000, Decode(rows, new DecodeOptions(Full: true))["MainBody"]!["value"]!.GetValue<string>().Length);

        var fields = Decode(rows, new DecodeOptions(Fields: new HashSet<string>(["mainbody"], StringComparer.OrdinalIgnoreCase)));
        Assert.Single(fields);
        Assert.Equal(1000, fields["MainBody"]!["value"]!.GetValue<string>().Length);
    }

    [Fact]
    public void Empty_properties_are_omitted_unless_all_properties()
    {
        PropertyRow[] rows = [new(123, Heading, English, String: "Only this"), new(123, MainBody, English, LongString: "")];

        Assert.Equal(["Heading"], Decode(rows).Select(p => p.Key));

        var all = Decode(rows, new DecodeOptions(AllProperties: true));
        Assert.Equal(12, all.Count);
        Assert.Equal("""{"type":"ContentArea","value":null}""", all["MainArea"]!.ToJsonString());
    }

    [Fact]
    public void Expand_inlines_the_target_properties()
    {
        var lookup = new FakeLookup();
        lookup.Expansions[TeaserGuid] = new JsonObject { ["Text"] = new JsonObject { ["type"] = "XhtmlString", ["value"] = "<p>Hi</p>" } };
        PropertyRow[] rows = [new(123, MainArea, English, LongString: $"""<div data-contentguid="{TeaserGuid}">{Body}</div>""")];

        Assert.Null(Decode(rows, lookup: lookup)["MainArea"]!["value"]![0]!["properties"]);
        var expanded = Decode(rows, new DecodeOptions(Expand: true), lookup)["MainArea"]!["value"]![0]!;
        Assert.Equal("<p>Hi</p>", expanded["properties"]!["Text"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Collector_records_every_reference()
    {
        var collector = new ReferenceCollector();
        new PropertyDecoder(Create(), collector, new DecodeOptions(), English, _ => "en").Decode(ArticlePage, PropertyTree.Build(
        [
            new PropertyRow(123, RelatedPage, English, ContentLink: 456),
            new PropertyRow(123, MainBody, English, LongString: $"""<a href="~/link/{PageGuid:N}.aspx">x</a>"""),
            new PropertyRow(123, MainArea, English, LongString: $"""<div data-contentguid="{TeaserGuid}">{Body}</div>"""),
        ]));

        Assert.Equal([456], collector.Ids);
        Assert.Equal(new HashSet<Guid> { PageGuid, TeaserGuid }, collector.Guids);
    }

    private static JsonObject DecodeTeaser(IEnumerable<PropertyRow> rows) =>
        new PropertyDecoder(Create(), new FakeLookup(), new DecodeOptions(), English, _ => "en").Decode(TeaserBlock, PropertyTree.Build(rows));

    [Fact]
    public void Dates_are_utc_as_the_cms_stores_them()
    {
        var properties = DecodeTeaser([new PropertyRow(789, TeaserStart, English, Date: new DateTime(2024, 5, 1, 8, 30, 0))]);

        Assert.Equal("2024-05-01T08:30:00Z", properties["StartDate"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Custom_long_string_types_that_store_json_are_shown_structured()
    {
        var properties = DecodeTeaser(
        [
            new PropertyRow(789, TeaserOptions, English, LongString: """[{"Caption":"Yes","Value":"1","Checked":false}]"""),
        ]);

        Assert.Equal("""[{"Caption":"Yes","Value":"1","Checked":false}]""", properties["Options"]!["value"]!.ToJsonString());
        Assert.Equal("Rubbish", DecodeTeaser([new PropertyRow(789, TeaserOptions, English, LongString: "Rubbish")])["Options"]!["value"]!.GetValue<string>());
        Assert.Equal("[not json", DecodeTeaser([new PropertyRow(789, TeaserOptions, English, LongString: "[not json")])["Options"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Lists_of_references_inside_block_list_items_are_arrays()
    {
        // IList<PageReference> in a block list item: one row per list item, indexed on the leaf segment, no trailing dot.
        var properties = Decode(
        [
            new PropertyRow(123, FactLabel, English, $".{Facts}(0).{FactLabel}.", String: "Offices"),
            new PropertyRow(123, FactSources, English, $".{Facts}(0).{FactSources}(1)", ContentLink: 999),
            new PropertyRow(123, FactSources, English, $".{Facts}(0).{FactSources}(0)", ContentLink: 456),
        ]);

        var sources = properties["Facts"]!["value"]![0]!["Sources"]!;
        Assert.Equal("PageReference", sources["type"]!.GetValue<string>());
        Assert.Equal(["456", "999"], sources["value"]!.AsArray().Select(r => r!["ref"]!.GetValue<string>()));
    }
}
