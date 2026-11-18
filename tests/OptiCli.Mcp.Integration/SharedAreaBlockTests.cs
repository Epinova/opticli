using System.Net;
using System.Text.Json;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// New blocks in a ContentArea where the site's edit UI makes them shared (<c>UIOptions.InlineBlocksInContentAreaEnabled</c>
/// off: CMS 12's default, <c>serve.sh --inline-blocks off</c> on CMS 13), written as the editor: shared blocks in the page's
/// "For this page" folder, saved as drafts with it and published along by <c>publish_content</c>; planned in a dry run; not
/// made for an editor who may not create content there; through a site's own write tool too; and the review refusal. The
/// server instructions say so only on such a site.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class SharedAreaBlockTests(SharedSessions sessions)
{
    private const string Shared = "shared";

    private const string InstructionsLine = "This site makes a new block in a ContentArea ({type, properties}) a shared block in the content's \"For this page\" folder";

    private static string ScratchName() => $"opticli-mcp-it {Guid.NewGuid():N}";

    private static object Block(string text, string? name = null) => name is null
        ? new { type = "EditorialBlock", properties = new { MainBody = $"<p>{text}</p>" } }
        : new { type = "EditorialBlock", name, properties = new { MainBody = $"<p>{text}</p>" } };

    private static List<JsonElement> SharedBlocks(JsonElement result) =>
        result.TryGetProperty("sharedBlocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array ? [.. blocks.EnumerateArray()] : [];

    private static string Status(JsonElement content) => content.GetProperty("status").GetString()!;

    /// <summary>The page as a visitor gets it, at the public address a result gives.</summary>
    private static async Task<string> RenderAsync(JsonElement published)
    {
        // The address is on the site's primary host; the test site answers it on its own port.
        var url = new Uri(McpSiteSettings.Url!, published.GetProperty("url").GetString());
        using var http = new HttpClient { BaseAddress = McpSiteSettings.Url };
        using var response = await http.GetAsync(url.PathAndQuery);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}");
        return html;
    }

    [McpSiteFact]
    public async Task The_server_instructions_explain_shared_area_blocks_only_where_the_site_makes_them()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        if (McpSiteSettings.InlineBlocks is not { } mode)
        {
            return;
        }

        var instructions = editor.Client.ServerInstructions!;

        if (mode == Shared)
        {
            Assert.Contains(InstructionsLine, instructions);
        }
        else
        {
            Assert.DoesNotContain(InstructionsLine, instructions);
            Assert.DoesNotContain("sharedBlocks", instructions);
        }
    }

    [McpInlineBlockModeFact(Shared)]
    public async Task An_editor_makes_a_page_with_area_blocks_as_drafts_in_its_folder_and_publish_content_publishes_them_along()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        var create = new { type = "StandardPage", name, parent = start, properties = new { MainContentArea = new[] { Block("Shared intro text", "Intro"), Block("Shared second text") } } };

        // The dry run says what each block will be, and makes nothing.
        var dry = await editor.OkAsync("create_content", new { create.type, create.name, create.parent, create.properties, dryRun = true });
        Assert.True(dry.GetProperty("valid").GetBoolean(), dry.GetRawText());
        var planned = SharedBlocks(dry);
        Assert.Equal(2, planned.Count);
        Assert.All(planned, b => Assert.Equal(JsonValueKind.Null, b.TryGetProperty("ref", out var r) ? r.ValueKind : JsonValueKind.Null));
        Assert.Contains("\"shared\":\"planned\"", dry.GetProperty("changes").GetRawText());
        Assert.Empty((await editor.OkAsync("find_content", new { name, root = start })).GetProperty("items").EnumerateArray());

        var made = await editor.OkAsync("create_content", create);
        var page = WritingTests.Id(made.GetProperty("content"));
        try
        {
            var blocks = SharedBlocks(made);
            Assert.Equal(["created", "created"], blocks.Select(b => b.GetProperty("action").GetString()));
            Assert.Equal("Intro", blocks[0].GetProperty("name").GetString());
            Assert.Single(blocks.Select(b => b.GetProperty("folder").GetString()).Distinct());
            // Drafts in the editor's name, which the area shows by their refs.
            foreach (var block in blocks)
            {
                var read = await editor.OkAsync("get_content", new { reference = block.GetProperty("ref").GetString()!.Split('_')[0], version = "latest" });
                Assert.Equal(("checkedOut", block.GetProperty("folder").GetString()), (Status(read), read.GetProperty("parent").GetString()));
            }
            var area = (await editor.OkAsync("get_content", new { reference = page, version = "latest" })).GetProperty("properties").GetProperty("MainContentArea").GetProperty("value");
            Assert.All(area.EnumerateArray(), i => Assert.True(i.TryGetProperty("ref", out _)));

            // Published, the page takes its blocks along, and the site renders them.
            var published = await editor.OkAsync("publish_content", new { reference = page });
            Assert.Equal(["published", "published"], SharedBlocks(published).Select(b => b.GetProperty("action").GetString()));
            foreach (var block in blocks)
            {
                Assert.Equal("published", Status(await editor.OkAsync("get_content", new { reference = block.GetProperty("ref").GetString()!.Split('_')[0], version = "latest" })));
            }
            var html = await RenderAsync(published);
            Assert.Contains("Shared intro text", html);
            Assert.Contains("Shared second text", html);
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpInlineBlockModeFact(Shared)]
    public async Task An_editor_who_may_not_create_content_on_the_page_gets_no_new_block_in_its_area()
    {
        // The product editor may edit the hidden page, not create content there (McpFixture.cs): the edit UI offers no
        // "Create a new block" then, and neither does the module.
        var product = await sessions.ForAsync(TestUsers.Product);
        foreach (var dryRun in new[] { true, false })
        {
            var refused = await product.ErrorAsync("update_content", new
            {
                reference = TestUsers.HiddenPage,
                areaOps = new[] { new { op = "add", property = "MainContentArea", type = "EditorialBlock", values = new { MainBody = "<p>Not made</p>" } } },
                dryRun,
            });
            Assert.Equal("refused", refused.GetProperty("code").GetString());
        }
    }

    [McpInlineBlockModeFact(Shared)]
    public async Task A_review_request_with_a_new_block_is_refused_with_how_to_go_about_it()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var name = ScratchName();

        var refused = await editor.ErrorAsync("create_content", new
        {
            type = "StandardPage", name, parent = TestUsers.ApprovalRoot, requestApproval = true,
            properties = new { MainContentArea = new[] { Block("For review") } },
        });

        Assert.Equal("usage", refused.GetProperty("code").GetString());
        Assert.Contains("MainContentArea[0] (a new block)", refused.GetProperty("message").GetString());
        Assert.StartsWith("Save the change into a CMS project instead (project, ready and requestApproval)", refused.GetProperty("hint").GetString());
        Assert.Empty((await editor.OkAsync("find_content", new { name, root = TestUsers.ApprovalRoot })).GetProperty("items").EnumerateArray());
    }

    [McpSiteFact]
    public async Task A_sites_own_write_tool_makes_a_new_area_block_as_update_content_does()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        if (McpSiteSettings.InlineBlocks is not { } mode)
        {
            return;
        }
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            var arguments = new Dictionary<string, object?> { ["ids"] = page, ["text"] = "Teaser", ["newBlock"] = "Tool block" };
            var dry = await editor.OkAsync("fixture_set_teaser", new Dictionary<string, object?>(arguments) { ["dryRun"] = true });
            Assert.True(dry.GetProperty("valid").GetBoolean(), dry.GetRawText());
            var run = await editor.OkAsync("fixture_set_teaser", new Dictionary<string, object?>(arguments) { ["planToken"] = dry.GetProperty("planToken").GetString() });

            var step = Assert.Single(run.GetProperty("steps").EnumerateArray());
            var item = (await editor.OkAsync("get_content", new { reference = page, version = "latest" })).GetProperty("properties").GetProperty("MainContentArea").GetProperty("value")[0];
            if (mode == Shared)
            {
                var block = Assert.Single(SharedBlocks(step));
                Assert.Equal(("created", "Tool block"), (block.GetProperty("action").GetString(), block.GetProperty("name").GetString()));
                Assert.Equal(block.GetProperty("ref").GetString()!.Split('_')[0], item.GetProperty("ref").GetString());
                Assert.Equal("checkedOut", Status(await editor.OkAsync("get_content", new { reference = item.GetProperty("ref").GetString(), version = "latest" })));
            }
            else
            {
                Assert.Empty(SharedBlocks(step));
                Assert.True(item.GetProperty("inline").GetBoolean());
            }
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpInlineBlockModeFact("inline")]
    public async Task Where_the_site_makes_inline_blocks_a_new_area_block_is_inline_and_nothing_else_is_made()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var made = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, properties = new { MainContentArea = new[] { Block("Inline text") } } });
        var page = WritingTests.Id(made.GetProperty("content"));
        try
        {
            Assert.Empty(SharedBlocks(made));
            var item = (await editor.OkAsync("get_content", new { reference = page, version = "latest" })).GetProperty("properties").GetProperty("MainContentArea").GetProperty("value")[0];
            Assert.True(item.GetProperty("inline").GetBoolean());
            var published = await editor.OkAsync("publish_content", new { reference = page });
            Assert.Empty(SharedBlocks(published));
            Assert.Contains("Inline text", await RenderAsync(published));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }
}
