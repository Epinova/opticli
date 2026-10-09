using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>
/// CMS 13: a Visual Builder composition as a write gives it, in <c>get</c>'s shape or the shape writes report it in, turned
/// into what the site agent takes.
/// </summary>
public class CompositionInputTests
{
    private const int Experience = 80;
    private const int Section = 81;
    private const int TextElement = 82;
    private const int Page = 84;
    private const int Banner = 85;

    private static readonly CmsModel Model = new(
        [
            Type(Experience, "VbExperience", ContentKind.Experience, "Experience"),
            Type(Section, "VbSection", ContentKind.Section, "Section"),
            Type(TextElement, "VbTextElement", ContentKind.Element, "Block"),
            Type(Page, "StandardPage", ContentKind.Page, "Page"),
            Type(Banner, "VbBanner", ContentKind.Block, "Block"),
        ],
        [
            new(801, Experience, "Layout", Compositions.LayoutType, PropertyBaseType.Json, null, true, false),
            new(802, Experience, "UnstructuredData", "ContentArea", PropertyBaseType.LongString, null, true, false),
            new(803, Experience, "Summary", "LongString", PropertyBaseType.LongString, null, true, false),
            new(811, Section, "Layout", Compositions.LayoutType, PropertyBaseType.Json, null, true, false),
            new(812, Section, "UnstructuredData", "ContentArea", PropertyBaseType.LongString, null, true, false),
            new(821, TextElement, "Heading", "LongString", PropertyBaseType.LongString, null, true, false),
            new(822, TextElement, "Body", "XhtmlString", PropertyBaseType.LongString, null, true, false),
            new(823, TextElement, "Target", "ContentReference", PropertyBaseType.ContentReference, null, false, false),
            new(824, TextElement, "Links", "LinkCollection", PropertyBaseType.LongString, null, false, false),
            new(825, TextElement, "Cards", "ContentArea", PropertyBaseType.LongString, null, false, false),
            new(841, Page, "Composition", "LongString", PropertyBaseType.LongString, null, true, false),
            new(851, Banner, "Title", "LongString", PropertyBaseType.LongString, null, true, false),
        ],
        [new LanguageBranch(1, "en", "English", null, true)],
        [],
        null,
        null);

    private static ContentTypeInfo Type(int id, string name, ContentKind kind, string typeBase) => new(id, Guid.NewGuid(), name, null, null, kind, typeBase, null, 0);

    private static readonly CompositionInput.Resolvers Resolve = new(
        reference => Task.FromResult(reference == "/en/shared/" ? "103" : reference),
        blueprint => Task.FromResult(Guid.Parse("b889b8fc-18ef-4e80-8fd9-3945ab98337c")));

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Text(CompositionNodeValue value) => JsonSerializer.Serialize(value, AgentJson.Options);

    /// <summary>What <c>get 147 --fields composition</c> shows, shortened.</summary>
    private const string FromGet = """
        {"layout":"outline","culture":"en","displayTemplate":"page","sections":[
          {"key":"7709df0e-51f3-4c11-a38a-7dca12cef6db","nodeType":"component","name":"Banner","type":"VbBanner","inline":true,"properties":{"Title":{"type":"LongString","value":"A banner"}}},
          {"key":"75b75b29-3134-4443-968d-73e85d32b44e","name":"Cards","type":"VbSection","inline":true,"rows":[
            {"key":"d0140350-0381-4394-a374-6a421947e133","name":"Card row","displayTemplate":"vbRow","displaySettings":{"gap":"wide"},"columns":[
              {"key":"140e7ba3-c737-4608-82aa-f9c1f3283490","name":"First","elements":[
                {"key":"468e98b1-913c-48dd-9409-16cb752fbd66","name":"Card","type":"VbTextElement","inline":true,"properties":{
                  "Heading":{"type":"LongString","value":"Card"},
                  "Target":{"type":"ContentReference","value":{"ref":"6","guid":"7b08c7d5-7585-47e5-add7-5822da68c3ce","type":"StartPage","name":"Start","url":"/en/"}},
                  "Links":{"type":"LinkCollection","value":[{"text":"Start","href":"~/link/7b08c7d5758547e5add75822da68c3ce.aspx","content":{"ref":"6","name":"Start"}}]},
                  "Body":{"type":"XhtmlString","value":"<p>Text</p>","links":[{"ref":"6"}]}}},
                {"key":"f8bddd30-28c4-47cb-a2ba-002e59368220","name":"Shared again","type":"VbTextElement","content":{"ref":"103","guid":"7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02","type":"VbTextElement","name":"VB shared element","status":"published","kind":"element"}}]}]}]}]}
        """;

    [Fact]
    public async Task Gets_shape_is_taken_as_it_is_with_values_unwrapped_and_references_as_refs()
    {
        var value = await CompositionInput.RootAsync(Model, Json(FromGet), Resolve);

        Assert.Equal("page", value.DisplayTemplate);
        var banner = value.Nodes![0];
        Assert.Equal(("component", "Banner", "VbBanner"), (banner.NodeType, banner.Name, banner.Type));
        Assert.Equal("\"A banner\"", banner.Properties!["Title"].GetRawText());
        var cards = value.Nodes[1];
        Assert.Equal("section", cards.NodeType);
        var row = cards.Nodes!.Single();
        Assert.Equal(("row", "vbRow", "wide"), (row.NodeType, row.DisplayTemplate, row.DisplaySettings!["gap"]));
        var column = row.Nodes!.Single();
        Assert.Equal("column", column.NodeType);
        var card = column.Nodes![0];
        Assert.Equal("component", card.NodeType);
        Assert.Equal("\"6\"", card.Properties!["Target"].GetRawText());
        Assert.Equal("[{\"text\":\"Start\",\"href\":\"~/link/7b08c7d5758547e5add75822da68c3ce.aspx\"}]", card.Properties["Links"].GetRawText());
        Assert.Equal("<p>Text</p>", card.Properties["Body"].GetString());
        var shared = column.Nodes[1];
        Assert.Equal(("103", null), (shared.Ref, shared.Type));
        Assert.Equal("f8bddd30-28c4-47cb-a2ba-002e59368220", shared.Key);
    }

    [Fact]
    public async Task The_shape_writes_report_it_in_is_taken_too_with_shared_refs_resolved()
    {
        var value = await CompositionInput.RootAsync(Model, Json("""
            {"nodes":[{"nodeType":"section","type":"VbSection","nodes":[{"nodeType":"row","nodes":[{"nodeType":"column","nodes":[
              {"nodeType":"component","ref":"/en/shared/","name":"Shared"},
              {"nodeType":"component","type":"VbTextElement","properties":{"Heading":"Plain"}}]}]}]},
              {"blueprint":"VB section blueprint","nodeType":"section"}]}
            """), Resolve);

        var column = value.Nodes![0].Nodes![0].Nodes![0];
        Assert.Equal("103", column.Nodes![0].Ref);
        Assert.Equal("\"Plain\"", column.Nodes[1].Properties!["Heading"].GetRawText());
        Assert.Equal(Guid.Parse("b889b8fc-18ef-4e80-8fd9-3945ab98337c"), value.Nodes[1].Blueprint);
    }

    [Fact]
    public async Task A_shared_block_in_an_elements_ContentArea_keeps_its_ref_object()
    {
        var value = await CompositionInput.RootAsync(Model, Json("""
            {"sections":[{"type":"VbSection","rows":[{"columns":[{"elements":[{"type":"VbTextElement","properties":{"Cards":[
              {"ref":"37"},{"ref":"38","type":"VbBanner","name":"Shared","status":"published"},{"inline":true,"type":"vbbanner","properties":{"Title":{"type":"LongString","value":"Inline"}}}]}}]}]}]}]}
            """), Resolve);

        var element = value.Nodes![0].Nodes![0].Nodes![0].Nodes![0];
        Assert.Equal("""[{"ref":"37"},{"ref":"38"},{"type":"VbBanner","properties":{"Title":"Inline"}}]""", element.Properties!["Cards"].GetRawText());

        // composition set, whose node type the site looks up: an area known by its name.
        var change = CompositionInput.Change(Model, Json("""{"properties":{"Cards":[{"ref":"37"}]}}"""), "change");
        Assert.Equal("""[{"ref":"37"}]""", change.Properties!["Cards"].GetRawText());
    }

    [Theory]
    [InlineData("""{"sections":[{"type":"VbSection","colour":"red"}]}""", "unknown field \"colour\"")]
    [InlineData("""{"sections":[{"key":"x","name":"Gone","missing":true}]}""", "missing")]
    [InlineData("""{"sections":[],"unplaced":[{"key":"x"}]}""", "unplaced")]
    [InlineData("""{"sections":[{"type":"VbSection","rows":[],"nodes":[]}]}""", "rows and nodes")]
    [InlineData("""{"sections":[{"type":"VbSection","rows":[{"columns":[{"elements":[{"type":"VbTextElement","properties":{"Headline":"x"}}]}]}]}]}""", "'Headline' is not a property of VbTextElement")]
    [InlineData("""{"sections":[{"type":"VbSection","rows":[{"columns":[{"elements":[{"type":"VbTextElement","properties":{"Body":{"type":"XhtmlString","value":"<p>cut","truncated":true,"length":9000}}}]}]}]}]}""", "cut short by get")]
    [InlineData("""{"rows":[{"nodeType":"section"}]}""", "is a section, where a row goes")]
    [InlineData("""{"sections":[{"type":"NoSuchType"}]}""", "NoSuchType")]
    public async Task What_cant_be_written_back_is_refused_with_why(string json, string message)
    {
        var refused = await Assert.ThrowsAnyAsync<OptiCliException>(() => CompositionInput.RootAsync(Model, Json(json), Resolve));

        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public async Task An_unknown_property_names_the_node_and_on_a_keyed_node_that_it_keeps_its_block()
    {
        var unkeyed = await Assert.ThrowsAsync<UsageException>(() => CompositionInput.RootAsync(Model, Json("""
            {"sections":[{"type":"VbSection","rows":[{"columns":[{"elements":[{"type":"VbTextElement","properties":{"Headline":"x"}}]}]}]}]}
            """), Resolve));
        Assert.StartsWith("composition.sections[0].rows[0].columns[0].elements[0]: 'Headline' is not a property of VbTextElement", unkeyed.Message);
        Assert.DoesNotContain("keeps its block", unkeyed.Hint);

        var keyed = await Assert.ThrowsAsync<UsageException>(() => CompositionInput.RootAsync(Model, Json("""
            {"sections":[{"type":"VbSection","rows":[{"columns":[{"elements":[{"key":"5faf72ec-0000-0000-0000-000000000001","type":"VbTextElement","properties":{"Link":"https://example.com/"}}]}]}]}]}
            """), Resolve));
        Assert.Contains("keeps its block", keyed.Hint);
    }

    [Fact]
    public void Split_takes_the_composition_out_of_the_values_of_an_experience_also_as_JSON_text()
    {
        var properties = Json("""{"Summary":"S","composition":"{\"sections\":[]}"}""");

        var (rest, composition) = CompositionInput.Split(Model, Experience, properties);

        Assert.Equal("""{"Summary":"S"}""", rest!.ToJsonString());
        Assert.Equal("""{"sections":[]}""", composition!.ToJsonString());
        Assert.Null(CompositionInput.Split(Model, Experience, Json("""{"Composition":{}}""")).Properties);
    }

    [Fact]
    public void Split_leaves_a_property_named_composition_alone_and_refuses_one_for_content_without_a_composition()
    {
        var page = Json("""{"Composition":"text"}""");

        Assert.Same(page, CompositionInput.Split(Model, Page, page).Properties);
        var refused = Assert.Throws<UsageException>(() => CompositionInput.Split(Model, TextElement, Json("""{"composition":{}}""")));
        Assert.Contains("VbTextElement has no Visual Builder composition", refused.Message);
    }

    [Fact]
    public void A_change_takes_a_name_template_settings_and_properties_and_nothing_else()
    {
        var change = CompositionInput.Change(Model, Json("""{"name":"N","displayTemplate":"","displaySettings":{"color":null,"wide":true},"properties":{"Heading":{"type":"LongString","value":"H"}}}"""), "the change");

        Assert.Equal("""{"name":"N","displayTemplate":"","displaySettings":{"color":null,"wide":"true"},"properties":{"Heading":"H"}}""", Text(change));
        Assert.Contains("unknown field \"type\"", Assert.Throws<UsageException>(() => CompositionInput.Change(Model, Json("""{"type":"X"}"""), "the change")).Message);
    }

    [Fact]
    public void The_properties_a_composition_is_stored_in_arent_written_directly()
    {
        var layout = Assert.Throws<UsageException>(() => PropertyNameCheck.Check(Model, Experience, Json("""{"Layout":{}}""")));
        var items = Assert.Throws<UsageException>(() => PropertyNameCheck.RequireContentArea(Model, Experience, "unstructureddata"));

        Assert.Contains("'Layout' is where VbExperience stores its Visual Builder composition", layout.Message);
        Assert.Contains("opticli composition", layout.Hint);
        Assert.Contains("'UnstructuredData' is where", items.Message);
        PropertyNameCheck.Check(Model, Experience, Json("""{"Summary":"fine"}"""));
    }

    [Fact]
    public async Task One_node_takes_the_node_type_the_command_names()
    {
        var node = await CompositionInput.NodeAsync(Model, Json("""{"type":"VbSection","rows":[{"name":"R"}]}"""), "section", "the new node", Resolve);

        Assert.Equal(("section", "row"), (node.NodeType, node.Nodes![0].NodeType));
        Assert.Equal("component", (await CompositionInput.NodeAsync(Model, Json("""{"type":"VbTextElement"}"""), "element", "the new node", Resolve)).NodeType);
    }
}
