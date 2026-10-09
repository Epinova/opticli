using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// Inline blocks in a ContentArea (CMS 12.20+) written as the editor: made, changed by position and written back whole,
/// with the edit UI's rules for their properties and the content type's access rights for a new one.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class InlineBlockTests(SharedSessions sessions)
{
    private static string ScratchName() => $"opticli-mcp-it {Guid.NewGuid():N}";

    private static JsonElement Area(JsonElement content) => content.GetProperty("properties").GetProperty("MainContentArea").GetProperty("value");

    /// <summary>get_content's area as update_content takes it: an inline block's values without their <c>{type, value}</c>.</summary>
    private static JsonArray Writable(JsonElement area) => new(area.EnumerateArray().Select(item =>
    {
        var copy = JsonNode.Parse(item.GetRawText())!.AsObject();
        if (copy["properties"] is JsonObject properties)
        {
            copy["properties"] = Plain(properties);
        }
        return (JsonNode?)copy;
    }).ToArray());

    /// <summary>Values in get_content's shape as plain values, a local block's (and a block list's) too.</summary>
    private static JsonObject Plain(JsonObject properties) => new(properties.Select(p => KeyValuePair.Create(p.Key, p.Value?["value"] switch
    {
        JsonObject block when (string?)p.Value!["type"] == "Block" => Plain(block),
        JsonArray items when (string?)p.Value!["type"] == "BlockList" => new JsonArray(items.Select(i => (JsonNode?)Plain(i!.AsObject())).ToArray()),
        var value => value?.DeepClone(),
    })));

    [McpSiteFact]
    public async Task An_editor_adds_and_changes_inline_blocks_as_the_edit_UI_lets_them()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            // McpFieldsBlock (McpFixture.cs) has properties the edit UI hides or locks: an editor sets none of them.
            var added = await editor.OkAsync("update_content", new
            {
                reference = page,
                areaOps = new[] { new { op = "add", property = "MainContentArea", type = "McpFieldsBlock", values = new { Heading = "Inline" } } },
            });
            Assert.True(added.GetProperty("saved").GetBoolean());
            var item = Area(await editor.OkAsync("get_content", new { reference = page, version = "latest" }))[0];
            Assert.True(item.GetProperty("inline").GetBoolean());
            Assert.Equal("McpFieldsBlock", item.GetProperty("type").GetString());
            var shown = item.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
            Assert.Contains("Heading", shown);
            Assert.DoesNotContain("Hidden", shown);
            Assert.DoesNotContain("Locked", shown);

            foreach (var property in new[] { "Hidden", "Locked", "AdminScripts" })
            {
                var byPosition = await editor.ErrorAsync("update_content", new { reference = page, properties = new Dictionary<string, object> { ["MainContentArea[0]"] = new Dictionary<string, string> { [property] = "x" } }, dryRun = true });
                Assert.Contains("not editable in the CMS edit UI for you", byPosition.GetProperty("message").GetString());
                var asNew = await editor.ErrorAsync("update_content", new
                {
                    reference = page,
                    areaOps = new[] { new { op = "add", property = "MainContentArea", type = "McpFieldsBlock", values = new Dictionary<string, string> { [property] = "x" } } },
                    dryRun = true,
                });
                Assert.Contains("not editable in the CMS edit UI for you", asNew.GetProperty("message").GetString());
            }
            await editor.OkAsync("update_content", new { reference = page, properties = new Dictionary<string, object> { ["MainContentArea[0]"] = new { Heading = "Changed" } } });

            // A type the editor may not create (RestrictedBlock: only WebAdmins) isn't made inline either.
            var restricted = await editor.ErrorAsync("update_content", new
            {
                reference = page,
                areaOps = new[] { new { op = "add", property = "MainContentArea", type = "RestrictedBlock", values = new { Heading = "x" } } },
                dryRun = true,
            });
            Assert.Contains("may not create RestrictedBlock", restricted.GetProperty("message").GetString());

            // The area as get_content shows it, written back with plain values, changes nothing.
            var area = Area(await editor.OkAsync("get_content", new { reference = page, version = "latest" }));
            Assert.Equal("Changed", area[0].GetProperty("properties").GetProperty("Heading").GetProperty("value").GetString());
            var same = await editor.OkAsync("update_content", new { reference = page, properties = new Dictionary<string, JsonNode> { ["MainContentArea"] = Writable(area) } });
            Assert.False(same.GetProperty("saved").GetBoolean());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }
}
