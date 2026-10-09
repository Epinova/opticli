using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Integration.Comparison;
using OptiCli.Integration.Sampling;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>The comparison machinery itself, on hand-written values; runs without a site.</summary>
public class ComparisonTests
{
    [Fact]
    public void Sample_covers_every_kind_and_type_before_repeating_one()
    {
        var candidates = Enumerable.Range(1, 60).Select(i => new SampleItem(i, TypeId: i % 6, Kind: (ContentKind)(i % 3), LanguageId: i % 2)).ToList();

        var sample = Stratified.Take(candidates, 6, seed: 1);

        Assert.Equal(3, sample.Select(s => s.Kind).Distinct().Count());
        Assert.Equal(6, sample.Select(s => s.TypeId).Distinct().Count());
        Assert.Equal(sample, Stratified.Take(candidates, 6, seed: 1));
    }

    [Fact]
    public void Db_and_cms_shapes_of_the_same_value_are_equal_when_canonical()
    {
        var db = JsonNode.Parse("""
            {
              "MainArea": {"type": "ContentArea", "culture": "en", "value": [
                {"ref": "789", "guid": "11111111-1111-1111-1111-111111111111", "type": "TeaserBlock", "name": "Teaser", "displayOption": "wide"},
                {"inline": true, "type": "TeaserBlock", "name": "Inline", "properties": {"Heading": {"type": "String", "value": "Hi"}}}]},
              "Related": {"type": "ContentReference", "value": {"ref": "456", "type": "ArticlePage", "url": "/en/about/"}},
              "Starts": {"type": "Date", "value": "2024-05-01T08:30:00Z"},
              "Empty": {"type": "String", "value": null}
            }
            """)!.AsObject();
        var cms = new Dictionary<string, ContentItemProperty>
        {
            ["MainArea"] = new("ContentArea", JsonSerializer.SerializeToElement(new[]
            {
                new ContentItemAreaEntry("789") { DisplayOption = "wide" },
                new ContentItemAreaEntry(null)
                {
                    Inline = true, Type = "TeaserBlock", Name = "Inline",
                    Properties = new Dictionary<string, ContentItemProperty> { ["Heading"] = new("String", JsonSerializer.SerializeToElement("Hi")) },
                },
            }, AgentJson.Options)),
            ["Related"] = new("ContentReference", JsonSerializer.SerializeToElement("456")),
            ["Starts"] = new("Date", JsonSerializer.SerializeToElement("2024-05-01T10:30:00+02:00")),
            ["Empty"] = new("String"),
        };

        Assert.Equal(Canonical.Text(Canonical.FromDb(db)), Canonical.Text(Canonical.FromAgent(cms)));
    }

    [Fact]
    public void A_db_composition_and_the_cms_mappers_are_equal_when_canonical()
    {
        var db = JsonNode.Parse("""
            {"layout":"outline","culture":"en","sections":[
              {"key":"k1","name":"Hero","type":"VbSection","inline":true,"displayTemplate":"vbSection","displaySettings":{"background":"dark"},"rows":[
                {"key":"r1","name":"Row","columns":[{"key":"c1","name":"Column","elements":[
                  {"key":"k2","name":"Intro","type":"VbTextElement","inline":true,"properties":{"Heading":{"type":"LongString","value":"Hi"}}},
                  {"key":"k3","name":"Shared","type":"VbTextElement","content":{"ref":"103","type":"VbTextElement","name":"Shared element"}},
                  {"key":"k9","name":"Gone","missing":true}]}]}]},
              {"key":"k4","name":"Banner","type":"VbBanner","inline":true,"displayTemplate":"vbBanner","properties":{"Title":{"type":"LongString","value":"Big"}}}],
             "unplaced":[{"key":"k8","name":"Loose","type":"VbTextElement","inline":true}]}
            """)!.AsObject();
        static Dictionary<string, ContentItemProperty> Text(string name, string value) => new() { [name] = new("LongString", JsonSerializer.SerializeToElement(value)) };
        var cms = new ContentItemCompositionNode("experience")
        {
            Key = "guid", Name = "Experience", Type = "VbExperience", LayoutType = "outline",
            Nodes =
            [
                new("section")
                {
                    Key = "k1", Name = "Hero", Type = "VbSection", LayoutType = "grid", DisplayTemplate = "vbSection",
                    DisplaySettings = new Dictionary<string, string> { ["background"] = "dark" }, Properties = new Dictionary<string, ContentItemProperty>(),
                    Nodes =
                    [
                        new("row")
                        {
                            Key = "r1", Name = "Row", LayoutType = "grid",
                            Nodes = [new("column") { Key = "c1", Name = "Column", Nodes = [
                                new("component") { Key = "k2", Name = "Intro", Type = "VbTextElement", LayoutType = "section", Properties = Text("Heading", "Hi") },
                                new("component") { Key = "k3", Name = "Shared", Type = "VbTextElement", Ref = "103" }] }],
                        },
                    ],
                },
                new("component") { Key = "k4", Name = "Banner", Type = "VbBanner", DisplayTemplate = "vbBanner", Properties = Text("Title", "Big") },
            ],
        };

        Assert.Equal(Canonical.Text(Canonical.CompositionFromDb(db)), Canonical.Text(Canonical.CompositionFromAgent(cms)));
    }

    [Fact]
    public void A_date_without_a_zone_is_not_taken_for_utc()
    {
        Assert.Equal("2024-05-01T08:30:00", Canonical.Date(JsonValue.Create("2024-05-01T08:30:00"))!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"https://www.example.com/shops/region/shop/\"", "\"https://www.example.com/shops/shop/\"", true)]
    [InlineData("\"https://www.example.com/shops/shop/\"", "\"https://www.example.com/shops/other/\"", false)]
    [InlineData("\"https://www.example.com/shops/region/shop/\"", "\"https://www.example.com/shops/region/\"", false)]
    public void Url_allowance_only_covers_ancestors_left_out(string db, string cms, bool allowed)
    {
        var mismatch = new Mismatch("123 [en]", "ShopPage", "page", MismatchKind.Identity, "url", null, "", db, cms);

        Assert.Equal(allowed, KnownDifferences.Find(mismatch)?.Name == "url-segments-left-out");
    }

    [Fact]
    public void Every_allow_list_entry_says_why()
    {
        Assert.All(KnownDifferences.All, k => Assert.True(k.Why.Length > 40, k.Name));
        Assert.Equal(KnownDifferences.All.Count, KnownDifferences.All.Select(k => k.Name).Distinct().Count());
    }
}
