using System.Text.Json;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;

namespace OptiCli.Agent.Tests.Content;

public class ShortcutValueTests
{
    private static ShortcutValue Parse(string json) => ShortcutValue.Parse(JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"none\"")]
    [InlineData("\"Normal\"")]
    [InlineData("{\"type\": \"normal\"}")]
    public void Nothing_means_no_shortcut(string json) => Assert.Equal(new ShortcutValue(ShortcutValue.Normal), Parse(json));

    [Theory]
    [InlineData("\"123\"", "123")]
    [InlineData("123", "123")]
    [InlineData("\"63__dam\"", "63__dam")]
    [InlineData("\"8c9ccb7a-963f-4d19-9672-7fd7356a2252\"", "8c9ccb7a-963f-4d19-9672-7fd7356a2252")]
    [InlineData("{\"to\": \"123\"}", "123")]
    [InlineData("{\"type\": \"Shortcut\", \"ref\": \"123\"}", "123")]
    [InlineData("{\"type\": \"shortcut\", \"to\": {\"ref\": \"123\", \"name\": \"As get shows it\"}}", "123")]
    public void A_ref_is_a_shortcut_to_that_page(string json, string to) =>
        Assert.Equal(new ShortcutValue(ShortcutValue.Shortcut, To: to), Parse(json));

    [Theory]
    [InlineData("\"https://example.com/a?b=1\"", "https://example.com/a?b=1")]
    [InlineData("\"mailto:post@example.com\"", "mailto:post@example.com")]
    [InlineData("\" /en/about/ \"", "/en/about/")]
    [InlineData("\"~/link/8c9ccb7a963f4d1996727fd7356a2252.aspx#x\"", "~/link/8c9ccb7a963f4d1996727fd7356a2252.aspx#x")]
    [InlineData("{\"url\": \"https://example.com/\"}", "https://example.com/")]
    public void A_url_is_an_external_link(string json, string url) =>
        Assert.Equal(new ShortcutValue(ShortcutValue.External, Url: url), Parse(json));

    [Fact]
    public void An_external_link_can_point_at_a_page_anchor_in_a_new_window() =>
        Assert.Equal(
            new ShortcutValue(ShortcutValue.External, To: "123", Anchor: "reports", Target: "_blank"),
            Parse("""{"type": "external", "to": "123", "anchor": "#reports", "target": "_blank"}"""));

    [Fact]
    public void Fetch_data_and_inactive_shortcuts()
    {
        Assert.Equal(new ShortcutValue(ShortcutValue.FetchData, To: "123"), Parse("""{"type": "fetchData", "to": "123"}"""));
        Assert.Equal(new ShortcutValue(ShortcutValue.Inactive), Parse("\"inactive\""));
    }

    [Theory]
    [InlineData("\"about-us\"", "neither a content ref nor a URL")]
    [InlineData("{\"type\": \"redirect\", \"to\": \"1\"}", "must be one of")]
    [InlineData("{\"type\": \"shortcut\"}", "needs \"to\"")]
    [InlineData("{\"type\": \"shortcut\", \"to\": \"1\", \"anchor\": \"x\"}", "takes \"to\", not")]
    [InlineData("{\"type\": \"external\"}", "either \"url\" or \"to\"")]
    [InlineData("{\"type\": \"external\", \"to\": \"1\", \"url\": \"https://x.no\"}", "either \"url\" or \"to\"")]
    [InlineData("{\"type\": \"inactive\", \"target\": \"_blank\"}", "takes no other fields")]
    [InlineData("{\"to\": \"1\", \"window\": \"_blank\"}", "Unknown shortcut field 'window'")]
    [InlineData("{}", "needs a type, a to or a url")]
    [InlineData("true", "Shortcut takes")]
    public void Mistakes_are_explained(string json, string message) =>
        Assert.Contains(message, Assert.Throws<AgentException>(() => Parse(json)).Message + " " + Assert.Throws<AgentException>(() => Parse(json)).Hint);
}
