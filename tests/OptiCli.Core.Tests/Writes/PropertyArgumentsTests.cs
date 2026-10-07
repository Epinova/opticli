using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class PropertyArgumentsTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private JsonObject Parse(string? json, params string[] assignments) => PropertyArguments.Parse(assignments, json, _root.Path);

    [Fact]
    public void Plain_values_are_strings_for_the_site_to_parse()
    {
        var result = Parse(null, "Heading=Hello world", "Priority=5", "ShowDate=true", "Url=https://example.com/?a=b");

        Assert.Equal("Hello world", (string?)result["Heading"]);
        Assert.Equal("5", (string?)result["Priority"]);
        Assert.Equal("true", (string?)result["ShowDate"]);
        // Only the first '=' separates name and value.
        Assert.Equal("https://example.com/?a=b", (string?)result["Url"]);
    }

    [Fact]
    public void An_empty_value_clears_the_property()
    {
        var result = Parse(null, "Heading=");

        Assert.True(result.ContainsKey("Heading"));
        Assert.Null(result["Heading"]);
    }

    [Fact]
    public void At_reads_a_file_relative_to_the_working_directory_and_double_at_escapes()
    {
        _root.Write("content/body.html", "<p>Hi</p>");

        var result = Parse(null, "MainBody=@content/body.html", "Handle=@@example");

        Assert.Equal("<p>Hi</p>", (string?)result["MainBody"]);
        Assert.Equal("@example", (string?)result["Handle"]);
    }

    [Fact]
    public void A_missing_file_is_a_usage_error()
    {
        var error = Assert.Throws<UsageException>(() => Parse(null, "MainBody=@missing.html"));
        Assert.Contains("missing.html", error.Message);
    }

    [Fact]
    public void Dotted_names_nest_into_local_block_objects()
    {
        var result = Parse(null, "Hero.Heading=Big", "Hero.SubHeading=Small", "Hero.Cta.Text=Go");

        var hero = Assert.IsType<JsonObject>(result["Hero"]);
        Assert.Equal("Big", (string?)hero["Heading"]);
        Assert.Equal("Small", (string?)hero["SubHeading"]);
        Assert.Equal("Go", (string?)hero["Cta"]!["Text"]);
    }

    [Theory]
    [InlineData("Hero=x", "Hero.Heading=y")]
    [InlineData("Hero.Heading=y", "Hero=x")]
    [InlineData("Heading=a", "heading=b")]
    public void Conflicting_assignments_are_rejected(string first, string second)
    {
        Assert.Throws<UsageException>(() => Parse(null, first, second));
    }

    [Theory]
    [InlineData("=value")]
    [InlineData("NoEquals")]
    [InlineData("Bad-Name=x")]
    [InlineData("Hero..Heading=x")]
    [InlineData("1Heading=x")]
    public void Malformed_assignments_are_usage_errors(string assignment)
    {
        Assert.Throws<UsageException>(() => Parse(null, assignment));
    }

    [Fact]
    public void Json_is_merged_on_top_recursively()
    {
        var result = Parse("""{"heading": "From JSON", "Hero": {"SubHeading": "S"}, "MainArea": [{"ref": "123"}]}""",
            "Heading=From args", "Hero.Heading=H", "Priority=1");

        // Same property, different case: JSON wins and keeps its own spelling.
        Assert.Equal("From JSON", (string?)result["heading"]);
        Assert.False(result.ContainsKey("Heading"));
        Assert.Equal("H", (string?)result["Hero"]!["Heading"]);
        Assert.Equal("S", (string?)result["Hero"]!["SubHeading"]);
        Assert.Equal("123", (string?)result["MainArea"]![0]!["ref"]);
        Assert.Equal("1", (string?)result["Priority"]);
    }

    [Fact]
    public void Json_replaces_a_non_object_with_an_object_and_vice_versa()
    {
        var result = Parse("""{"Hero": null, "Facts": {"Label": "L"}}""", "Hero.Heading=H", "Facts=x");

        Assert.Null(result["Hero"]);
        Assert.Equal("L", (string?)result["Facts"]!["Label"]);
    }

    [Fact]
    public void Values_in_gets_shape_are_unwrapped_as_set_takes_them()
    {
        var result = Parse("""
            {
              "MetaTitle": {"type": "LongString", "value": "Title", "culture": "en"},
              "ContactsPageLink": {"type": "ContentReference", "value": {"ref": "22", "guid": "588b2a04-0b50-483c-983c-51a40a53da65", "type": "ContainerPage", "name": "Contacts", "status": "published", "url": "/en/contacts/"}, "culture": "en"},
              "Links": {"type": "LinkCollection", "value": [{"text": "About", "href": "~/link/61104228962d4dd2925cb5b12e2373ee.aspx", "content": {"ref": "11", "name": "About us"}}]},
              "MainArea": {"type": "ContentArea", "value": [{"ref": "37", "guid": "3843b6e6-c614-415a-a4ff-ce39b32af13b", "type": "JumbotronBlock", "name": "J"}, {"ref": "38", "displayOption": "narrow"}]},
              "Logo": {"type": "Block", "blockType": "SiteLogotypeBlock", "value": {"Title": {"type": "LongString", "value": "Alloy"}, "Url": {"type": "Url", "value": "~/link/310b49a7f3b2488ab21b895fe69baa09.aspx", "target": {"ref": "35"}}}},
              "Plain": "as set takes it"
            }
            """);

        Assert.Equal("Title", (string?)result["MetaTitle"]);
        Assert.Equal("22", (string?)result["ContactsPageLink"]);
        Assert.Equal("""[{"text":"About","href":"~/link/61104228962d4dd2925cb5b12e2373ee.aspx"}]""", result["Links"]!.ToJsonString());
        // A ContentArea's items are what set takes already: kept whole.
        Assert.Equal("JumbotronBlock", (string?)result["MainArea"]![0]!["type"]);
        Assert.Equal("narrow", (string?)result["MainArea"]![1]!["displayOption"]);
        Assert.Equal("""{"Title":"Alloy","Url":"~/link/310b49a7f3b2488ab21b895fe69baa09.aspx"}""", result["Logo"]!.ToJsonString());
        Assert.Equal("as set takes it", (string?)result["Plain"]);
    }

    [Fact]
    public void A_value_get_cut_short_is_refused_and_an_object_that_only_looks_wrapped_is_kept()
    {
        var refused = Assert.Throws<UsageException>(() => Parse("""{"MainBody": {"type": "XhtmlString", "value": "<p>cut", "truncated": true, "length": 9000}}"""));
        Assert.Contains("MainBody was cut short by get", refused.Message);

        // A local block with properties of these names, given as set takes it: not get's shape (other keys, or no string type).
        var result = Parse("""{"Hero": {"type": "x", "value": "y", "Heading": "z"}, "Facts": {"type": 1, "value": "v"}}""");
        Assert.Equal("z", (string?)result["Hero"]!["Heading"]);
        Assert.Equal(1, (int?)result["Facts"]!["type"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    public void Json_must_be_an_object(string json)
    {
        Assert.Throws<UsageException>(() => Parse(json));
    }

    [Fact]
    public void The_request_shape_keeps_nulls_and_structure()
    {
        var request = PropertyArguments.ToRequest(Parse("""{"MainArea": [{"ref": "1"}]}""", "Heading=", "Hero.Heading=H"))!;

        Assert.Equal(System.Text.Json.JsonValueKind.Null, request["Heading"].ValueKind);
        Assert.Equal("H", request["hero"].GetProperty("Heading").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, request["MainArea"].ValueKind);
        Assert.Null(PropertyArguments.ToRequest(new JsonObject()));
    }
}
