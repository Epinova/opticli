using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Properties;

namespace OptiCli.Core.Tests.Properties;

/// <summary>
/// CMS 13's Visual Builder compositions, decoded from rows shaped like the ones the CMS stores (phase 0's findings): the
/// experience's <c>Layout</c> JSON and <c>UnstructuredData</c> markup, and each inline section's own pair as scoped rows.
/// </summary>
public class CompositionsTests
{
    private const int Experience = 80;
    private const int Section = 81;
    private const int TextElement = 82;
    private const int Banner = 83;
    private const int Page = 84;

    private const int Layout = 801;
    private const int Items = 802;
    private const int Summary = 803;
    private const int SectionLayout = 811;
    private const int SectionItems = 812;
    private const int Heading = 821;
    private const int Body = 822;
    private const int Title = 831;

    private const int English = 1;

    private static readonly Guid SharedGuid = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02");

    private static readonly CmsModel Model = new(
        [
            Type(Experience, "VbExperience", ContentKind.Experience, "Experience"),
            Type(Section, "VbSection", ContentKind.Section, "Section"),
            Type(TextElement, "VbTextElement", ContentKind.Element, "Block"),
            Type(Banner, "VbBanner", ContentKind.Block, "Block"),
            Type(Page, "StandardPage", ContentKind.Page, "Page"),
        ],
        [
            new(Layout, Experience, "Layout", Compositions.LayoutType, PropertyBaseType.Json, null, true, false),
            new(Items, Experience, "UnstructuredData", "ContentArea", PropertyBaseType.LongString, null, true, false),
            new(Summary, Experience, "Summary", "LongString", PropertyBaseType.LongString, null, true, false),
            new(SectionLayout, Section, "Layout", Compositions.LayoutType, PropertyBaseType.Json, null, true, false),
            new(SectionItems, Section, "UnstructuredData", "ContentArea", PropertyBaseType.LongString, null, true, false),
            new(Heading, TextElement, "Heading", "LongString", PropertyBaseType.LongString, null, true, false),
            new(Body, TextElement, "Body", "XhtmlString", PropertyBaseType.LongString, null, true, false),
            new(Title, Banner, "Title", "LongString", PropertyBaseType.LongString, null, true, false),
        ],
        [new LanguageBranch(English, "en", "English", null, true)],
        [],
        null,
        null);

    private static ContentTypeInfo Type(int id, string name, ContentKind kind, string typeBase) => new(id, Guid.NewGuid(), name, null, null, kind, typeBase, null, 0);

    private sealed class Lookup : IReferenceLookup
    {
        public ContentIdentity Content(int id) => ContentIdentity.MissingId(id);

        public ContentIdentity Content(Guid guid) => guid == SharedGuid
            ? new ContentIdentity("103", SharedGuid, "VbTextElement", "VB shared element", "en", "published", null)
            : ContentIdentity.MissingGuid(guid);

        public JsonObject? Expanded(Guid guid) => null;
    }

    /// <summary>Decodes the rows as <c>get</c> does and takes the composition out of the properties.</summary>
    private static (JsonObject? Composition, JsonObject Properties) Get(IEnumerable<PropertyRow> rows, int type = Experience, ContentKind kind = ContentKind.Experience)
    {
        var tree = PropertyTree.Build(rows);
        var properties = new PropertyDecoder(Model, new Lookup(), new DecodeOptions(), English, _ => "en").Decode(type, tree);
        return (Compositions.Extract(Model, type, kind, tree, properties), properties);
    }

    private static PropertyRow Row(int definition, string text, string? scope = null) => new(104, definition, English, scope, LongString: text);

    private const string Outline = """
        {"type":"outline","nodes":[
          {"type":"section","name":"Hero","propertyBinding":"UnstructuredData[k-hero]","displaySettings":{}},
          {"type":"component","name":"Banner","propertyBinding":"UnstructuredData[k-banner]","displayTemplateKey":"vbBanner","displaySettings":{"tone":"loud"}}],
         "displaySettings":{}}
        """;

    private static readonly string OutlineItems =
        $"""<div data-epi-block-id="k-hero" data-inlineblockname="Hero" data-inlineblocktypeid="{Section}">{"{}"}</div>"""
        + $"""<div data-epi-block-id="k-banner" data-inlineblockname="Banner" data-inlineblocktypeid="{Banner}">{"{}"}</div>""";

    private const string Grid = """
        {"type":"grid","nodes":[{"type":"row","name":"Two columns","id":"r1","displayTemplateKey":"vbRow","displaySettings":{"gap":"wide"},"nodes":[
          {"type":"column","name":"Left","id":"c1","nodes":[{"type":"component","name":"Intro","propertyBinding":"UnstructuredData[k-intro]","displayTemplateKey":"vbElement","displaySettings":{"color":"accent"}}],"displaySettings":{}},
          {"type":"column","name":"Right","id":"c2","nodes":[{"type":"component","name":"Shared","propertyBinding":"UnstructuredData[k-shared]","displaySettings":{}}],"displaySettings":{}}]}],
         "displayTemplateKey":"vbSection","displaySettings":{"background":"dark"}}
        """;

    private static readonly string GridItems =
        $"""<div data-epi-block-id="k-intro" data-inlineblockname="Intro" data-inlineblocktypeid="{TextElement}">{"{}"}</div>"""
        + $"""<div data-contentguid="{SharedGuid}" data-epi-block-id="k-shared" data-inlineblockname="Shared">{"{}"}</div>""";

    private static IEnumerable<PropertyRow> Fixture(string outline = Outline, string? outlineItems = null, string grid = Grid, string? gridItems = null) =>
    [
        Row(Layout, outline),
        Row(Items, outlineItems ?? OutlineItems),
        Row(Summary, "An experience"),
        Row(SectionLayout, grid, $".{Items}:{Section}(0).{SectionLayout}."),
        Row(SectionItems, gridItems ?? GridItems, $".{Items}:{Section}(0).{SectionItems}."),
        Row(Heading, "Welcome", $".{Items}:{Section}(0).{SectionItems}:{TextElement}(0).{Heading}."),
        Row(Body, "<p>Rich text</p>", $".{Items}:{Section}(0).{SectionItems}:{TextElement}(0).{Body}."),
        Row(Title, "A banner", $".{Items}:{Banner}(1).{Title}."),
    ];

    [Fact]
    public void An_experience_is_shown_as_sections_rows_columns_and_elements_in_place_of_its_storage()
    {
        var (composition, properties) = Get(Fixture());

        Assert.Equal(["Summary"], properties.Select(p => p.Key));
        Assert.Equal("""
            {"layout":"outline","culture":"en","sections":[
            {"key":"k-hero","name":"Hero","type":"VbSection","inline":true,"displayTemplate":"vbSection","displaySettings":{"background":"dark"},"rows":[
            {"key":"r1","name":"Two columns","displayTemplate":"vbRow","displaySettings":{"gap":"wide"},"columns":[
            {"key":"c1","name":"Left","elements":[{"key":"k-intro","name":"Intro","type":"VbTextElement","inline":true,"displayTemplate":"vbElement","displaySettings":{"color":"accent"},"properties":{"Body":{"type":"XhtmlString","value":"<p>Rich text</p>"},"Heading":{"type":"LongString","value":"Welcome"}}}]},
            {"key":"c2","name":"Right","elements":[{"key":"k-shared","name":"Shared","type":"VbTextElement","content":{"ref":"103","guid":"7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02","type":"VbTextElement","name":"VB shared element","language":"en","status":"published"}}]}]}]},
            {"key":"k-banner","nodeType":"component","name":"Banner","type":"VbBanner","inline":true,"displayTemplate":"vbBanner","displaySettings":{"tone":"loud"},"properties":{"Title":{"type":"LongString","value":"A banner"}}}]}
            """.ReplaceLineEndings(""), composition!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public void Storage_properties_named_with_fields_stay_as_they_are_beside_the_composition()
    {
        var tree = PropertyTree.Build(Fixture());
        var properties = new PropertyDecoder(Model, new Lookup(), new DecodeOptions(), English, _ => "en").Decode(Experience, tree);

        var composition = Compositions.Extract(Model, Experience, ContentKind.Experience, tree, properties, new HashSet<string>(["layout"], StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(composition);
        Assert.Equal(["Layout", "Summary"], properties.Select(p => p.Key).Order());
        Assert.Equal("outline", properties["Layout"]!["value"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void A_section_enabled_block_in_an_outline_says_it_is_a_component_and_a_section_doesnt()
    {
        var (composition, _) = Get(Fixture());

        var sections = composition!["sections"]!.AsArray();
        Assert.Null(sections[0]!["nodeType"]);
        Assert.Equal("component", sections[1]!["nodeType"]!.GetValue<string>());
    }

    [Fact]
    public void A_binding_that_finds_nothing_is_kept_as_missing_and_an_item_no_node_binds_as_unplaced()
    {
        var outline = """{"type":"outline","nodes":[{"type":"section","name":"Gone","propertyBinding":"UnstructuredData[k-gone]","displaySettings":{}},{"type":"section","name":"Hero","propertyBinding":"UnstructuredData[k-hero]"}]}""";

        var (composition, _) = Get(Fixture(outline));

        var sections = composition!["sections"]!.AsArray();
        Assert.Equal("""{"key":"k-gone","name":"Gone","missing":true}""", sections[0]!.ToJsonString());
        Assert.Equal("k-hero", sections[1]!["key"]!.GetValue<string>());
        var unplaced = Assert.Single(composition["unplaced"]!.AsArray());
        Assert.Equal(("k-banner", "VbBanner"), (unplaced!["key"]!.GetValue<string>(), unplaced["type"]!.GetValue<string>()));
    }

    [Fact]
    public void An_experience_or_section_with_nothing_stored_has_an_empty_composition()
    {
        Assert.Equal("""{"layout":"outline","sections":[]}""", Get([Row(Summary, "Empty")]).Composition!.ToJsonString());
        Assert.Equal("""{"layout":"grid","rows":[]}""", Get([], Section, ContentKind.Section).Composition!.ToJsonString());
    }

    [Fact]
    public void A_section_on_its_own_a_blueprint_is_a_grid_of_rows()
    {
        var (composition, properties) = Get(
        [
            Row(SectionLayout, Grid),
            Row(SectionItems, GridItems),
            Row(Heading, "Welcome", $".{SectionItems}:{TextElement}(0).{Heading}."),
        ], Section, ContentKind.Section);

        Assert.Empty(properties);
        Assert.Equal(("grid", "vbSection"), (composition!["layout"]!.GetValue<string>(), composition["displayTemplate"]!.GetValue<string>()));
        Assert.Equal("Welcome", composition["rows"]![0]!["columns"]![0]!["elements"]![0]!["properties"]!["Heading"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Content_without_a_layout_has_no_composition_and_an_unreadable_layout_is_left_as_stored()
    {
        Assert.Null(Get([Row(Title, "x")], Page, ContentKind.Page).Composition);

        var (composition, properties) = Get([Row(Layout, "{not json"), Row(Summary, "x")]);
        Assert.Null(composition);
        Assert.True(properties.ContainsKey("Layout"));
    }

    [Fact]
    public void Nodes_of_several_kinds_under_one_parent_are_listed_with_their_node_type()
    {
        var grid = """{"type":"grid","nodes":[{"type":"row","id":"r1","nodes":[]},{"type":"component","name":"Loose","propertyBinding":"UnstructuredData[k-intro]"}]}""";

        var (composition, _) = Get(Fixture(grid: grid));

        var nodes = composition!["sections"]![0]!["nodes"]!.AsArray();
        Assert.Equal(["row", "component"], nodes.Select(n => n!["nodeType"]!.GetValue<string>()));
    }

    [Fact]
    public void The_storage_properties_are_the_layout_and_its_items()
    {
        Assert.Equal(["Layout", "UnstructuredData"], Compositions.StorageProperties(Model, Experience));
        Assert.Empty(Compositions.StorageProperties(Model, Page));
        Assert.True(Compositions.IsLayouted(Model, Section));
    }
}
