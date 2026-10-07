using System.Text.Json.Nodes;
using OptiCli.Core.Output;

namespace OptiCli.Core.Tests.Output;

/// <summary>CMS 13: <c>get --text</c> shows a composition as the tree it is.</summary>
public class CompositionTextTests
{
    [Fact]
    public void A_composition_is_shown_as_its_tree_with_keys_types_styles_and_values()
    {
        var document = JsonNode.Parse("""
            {"ref":"147","name":"Second","composition":{"layout":"outline","culture":"en","sections":[
              {"key":"k-banner","nodeType":"component","name":"Banner","type":"VbBanner","inline":true,"properties":{"Title":{"type":"LongString","value":"A banner"}}},
              {"key":"k-cards","name":"Cards","type":"VbSection","inline":true,"rows":[
                {"key":"r1","name":"Card row","displayTemplate":"vbRow","displaySettings":{"gap":"wide"},"columns":[
                  {"key":"c1","name":"First","elements":[
                    {"key":"e1","name":"Card","type":"VbCardElement","inline":true,"properties":{"Image":{"type":"ContentReference","value":{"ref":"43","name":"charts.jpg"}},"Links":{"type":"LinkCollection","value":[{"text":"Start","href":"/"}]}}},
                    {"key":"e2","name":"Shared","type":"VbTextElement","content":{"ref":"103","name":"VB shared element","status":"published"}}]}]}]},
              {"key":"k-empty","name":"Empty","type":"VbSection","inline":true,"rows":[]}]}}
            """);

        var text = CompositionText.Document(document).ReplaceLineEndings("\n");

        Assert.Contains("ref:  147\nname: Second\ncomposition: outline, culture en\n", text);
        Assert.Contains("  component \"Banner\" [VbBanner, inline] key k-banner\n      Title: A banner\n", text);
        Assert.Contains("    row \"Card row\" key r1  template vbRow {gap=wide}\n", text);
        Assert.Contains("        element \"Card\" [VbCardElement, inline] key e1\n            Image: 43 \"charts.jpg\"\n            Links: Start\n", text);
        Assert.Contains("        element \"Shared\" [VbTextElement, shared 103 \"VB shared element\" published] key e2\n", text);
        Assert.Contains("  section \"Empty\" [VbSection, inline] key k-empty\n    (no rows)\n", text);
    }

    [Fact]
    public void A_document_without_a_composition_is_rendered_as_usual()
    {
        var document = JsonNode.Parse("""{"ref":"6","name":"Start"}""");

        Assert.Equal("ref:  6\nname: Start\n", CompositionText.Document(document).ReplaceLineEndings("\n"));
    }
}
