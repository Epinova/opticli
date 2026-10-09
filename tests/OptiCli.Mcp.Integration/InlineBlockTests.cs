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

    [McpSiteFact]
    public async Task An_editor_cant_drop_values_they_cant_change_by_writing_the_area_whole_but_may_change_one_block()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            // AdminScripts (McpFixture.cs) is locked for all but administrators: an administrator sets it.
            await admin.OkAsync("update_content", new
            {
                reference = page,
                areaOps = new[] { new { op = "add", property = "MainContentArea", type = "McpFieldsBlock", values = new Dictionary<string, string> { ["Heading"] = "Inline", ["AdminScripts"] = "admin only" } } },
            });

            // The editor's area, with the block changed: it isn't a copy, so it would be a new block without that value.
            var area = Writable(Area(await editor.OkAsync("get_content", new { reference = page, version = "latest" })));
            area[0]!["properties"]!["Heading"] = "Changed whole";
            var refused = await editor.ErrorAsync("update_content", new { reference = page, properties = new Dictionary<string, JsonNode> { ["MainContentArea"] = area }, dryRun = true });
            Assert.Equal("usage", refused.GetProperty("code").GetString());
            Assert.Contains("MainContentArea would lose MainContentArea[0].AdminScripts", refused.GetProperty("message").GetString());

            // One block's values by position keep the rest of it.
            await editor.OkAsync("update_content", new { reference = page, areaOps = new[] { new { op = "set", property = "MainContentArea", index = 0, values = new { Heading = "Changed" } } } });
            var kept = Area(await admin.OkAsync("get_content", new { reference = page, version = "latest" }))[0].GetProperty("properties");
            Assert.Equal(("Changed", "admin only"), (kept.GetProperty("Heading").GetProperty("value").GetString(), kept.GetProperty("AdminScripts").GetProperty("value").GetString()));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }
    [McpSiteFact]
    public async Task An_editor_cant_drop_values_they_cant_change_inside_an_inline_block_but_may_remove_a_nested_block_on_purpose()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            // An administrator's block, with values locked for everyone else in its own area's block and in its local block.
            var outer = JsonNode.Parse("""
                {"type": "McpFieldsBlock", "properties": {"Heading": "Outer",
                  "Area": [{"type": "McpFieldsBlock", "properties": {"Heading": "Nested", "AdminScripts": "nested-locked"}}],
                  "Inner": {"Title": "Inner", "AdminOnly": "inner-locked"}}}
                """)!;
            await admin.OkAsync("update_content", new { reference = page, properties = new Dictionary<string, JsonNode> { ["MainContentArea"] = new JsonArray(outer) } });

            // The area as the editor sees it, the outer block's heading changed: its local block would lose its locked value.
            var area = Writable(Area(await editor.OkAsync("get_content", new { reference = page, version = "latest" })));
            area[0]!["properties"]!["Heading"] = "Changed whole";
            var local = await editor.ErrorAsync("update_content", new { reference = page, properties = new Dictionary<string, JsonNode> { ["MainContentArea"] = area.DeepClone() }, dryRun = true });
            Assert.Equal(("usage", "unseenValues"), (local.GetProperty("code").GetString(), local.GetProperty("reason").GetString()));
            Assert.Contains("MainContentArea would lose MainContentArea[0].Inner.AdminOnly", local.GetProperty("message").GetString());

            // Without its area too, the nested block's locked value would go as well (it comes first).
            area[0]!["properties"]!.AsObject().Remove("Area");
            var nested = await editor.ErrorAsync("update_content", new { reference = page, properties = new Dictionary<string, JsonNode> { ["MainContentArea"] = area }, dryRun = true });
            Assert.Contains("MainContentArea would lose MainContentArea[0].Area[0].AdminScripts", nested.GetProperty("message").GetString());
            Assert.Contains("areaOps remove (property \"MainContentArea[0].Area\", index 0)", nested.GetProperty("hint").GetString());

            // Removed on purpose, by the nested area's path: the rest of the outer block stays, its locked values too.
            await editor.OkAsync("update_content", new { reference = page, areaOps = new[] { new { op = "remove", property = "MainContentArea[0].Area", index = 0 } } });
            var kept = Area(await admin.OkAsync("get_content", new { reference = page, version = "latest" }))[0].GetProperty("properties");
            Assert.Equal("Outer", kept.GetProperty("Heading").GetProperty("value").GetString());
            Assert.False(kept.TryGetProperty("Area", out var left) && left.TryGetProperty("value", out var items) && items.GetArrayLength() > 0);
            Assert.Equal("inner-locked", kept.GetProperty("Inner").GetProperty("value").GetProperty("AdminOnly").GetProperty("value").GetString());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }
}
