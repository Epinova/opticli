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
    public async Task Publish_content_leaves_a_live_blocks_newer_draft_behind_unless_named_and_its_dry_run_says_which()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var made = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, properties = new { MainContentArea = new[] { Block("Live original text", "Live") } } });
        var page = WritingTests.Id(made.GetProperty("content"));
        try
        {
            var live = SharedBlocks(made)[0].GetProperty("ref").GetString()!.Split('_')[0];
            await editor.OkAsync("publish_content", new { reference = page });
            // The editor's change to the live block in progress, a new block never published, and a new draft of the page.
            var liveBase = (await editor.OkAsync("get_content", new { reference = live, version = "latest" })).GetProperty("version").GetInt32();
            await editor.OkAsync("update_content", new { reference = live, properties = new { MainBody = "<p>Live changed text</p>" }, baseVersion = liveBase });
            var added = await editor.OkAsync("update_content", new { reference = page, areaOps = new[] { new { op = "add", property = "MainContentArea", type = "EditorialBlock", name = "Fresh", values = new { MainBody = "<p>Fresh text</p>" } } } });
            var fresh = SharedBlocks(added)[0].GetProperty("ref").GetString()!.Split('_')[0];

            // The dry run: the new block goes along, the live one stays behind, named in a warning; nothing is saved.
            var dry = await editor.OkAsync("publish_content", new { reference = page, dryRun = true });
            Assert.True(dry.GetProperty("dryRun").GetBoolean());
            Assert.Equal([fresh], SharedBlocks(dry).Select(b => b.GetProperty("ref").GetString()!.Split('_')[0]));
            var warning = Assert.Single(dry.GetProperty("validation").EnumerateArray(), v => v.GetProperty("severity").GetString() == "warning");
            Assert.Contains("('Live') at MainContentArea[0] is published already", warning.GetProperty("message").GetString());
            Assert.Contains($"blocks: [\"{live}\"]", warning.GetProperty("message").GetString());
            Assert.Equal("checkedOut", Status(await editor.OkAsync("get_content", new { reference = fresh, version = "latest" })));

            // A block from elsewhere can't be named.
            var elsewhere = await editor.ErrorAsync("publish_content", new { reference = page, blocks = new[] { start } });
            Assert.Equal("usage", elsewhere.GetProperty("code").GetString());

            // Published: the live block's change stays a draft.
            var published = await editor.OkAsync("publish_content", new { reference = page });
            Assert.Equal([fresh], SharedBlocks(published).Select(b => b.GetProperty("ref").GetString()!.Split('_')[0]));
            Assert.Equal("checkedOut", Status(await editor.OkAsync("get_content", new { reference = live, version = "latest" })));
            var html = await RenderAsync(published);
            Assert.Contains("Live original text", html);
            Assert.DoesNotContain("Live changed text", html);
            Assert.Contains("Fresh text", html);

            // The page is live now: publishing it again puts nothing live unnamed (a conflict whose hint names the live
            // block), dry run alike; named, its draft would go live on its own.
            foreach (var dryRun in new[] { true, false })
            {
                var nothing = await editor.ErrorAsync("publish_content", new { reference = page, dryRun });
                Assert.Equal("conflict", nothing.GetProperty("code").GetString());
                Assert.Contains($"{live} ('Live') at MainContentArea[0], published, with a newer draft", nothing.GetProperty("hint").GetString());
                Assert.Contains($"publish_content on {page} with blocks: [\"{live}\"]", nothing.GetProperty("hint").GetString());
            }
            var alone = await editor.OkAsync("publish_content", new { reference = page, blocks = new[] { live }, dryRun = true });
            Assert.Equal([live], SharedBlocks(alone).Select(b => b.GetProperty("ref").GetString()!.Split('_')[0]));
            Assert.Equal("checkedOut", Status(await editor.OkAsync("get_content", new { reference = live, version = "latest" })));

            // Named in blocks, its draft goes live with the page's next publish.
            var pageBase = (await editor.OkAsync("get_content", new { reference = page, version = "latest" })).GetProperty("version").GetInt32();
            await editor.OkAsync("update_content", new { reference = page, name = ScratchName(), baseVersion = pageBase });
            var named = await editor.OkAsync("publish_content", new { reference = page, blocks = new[] { live } });
            Assert.Equal([live], SharedBlocks(named).Select(b => b.GetProperty("ref").GetString()!.Split('_')[0]));
            Assert.Contains("Live changed text", await RenderAsync(named));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpInlineBlockModeFact(Shared)]
    public async Task A_whole_area_write_over_a_colleagues_draft_of_a_folder_block_needs_include_draft()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var made = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, properties = new { MainContentArea = new[] { Block("Intro original text", "Intro") } } });
        var page = WritingTests.Id(made.GetProperty("content"));
        try
        {
            var intro = SharedBlocks(made)[0].GetProperty("ref").GetString()!.Split('_')[0];
            await editor.OkAsync("publish_content", new { reference = page });
            // A colleague's change of the block in progress.
            var introBase = (await admin.OkAsync("get_content", new { reference = intro, version = "latest" })).GetProperty("version").GetInt32();
            var theirs = await admin.OkAsync("update_content", new { reference = intro, properties = new { MainBody = "<p>Colleague's text</p>" }, baseVersion = introBase });
            var theirVersion = theirs.GetProperty("content").GetProperty("ref").GetString();
            var area = new { MainContentArea = new[] { Block("Editor's text", "Intro") } };

            // Written whole, the area would save the block's new version over their draft: refused, the dry run too.
            foreach (var dryRun in new[] { true, false })
            {
                var refused = await editor.ErrorAsync("update_content", new { reference = page, properties = area, dryRun });
                Assert.Equal(("conflict", "pendingDraft"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
                var draft = refused.GetProperty("pendingDraft");
                Assert.Equal((theirVersion, TestUsers.Admin, "MainContentArea[0]"),
                    (draft.GetProperty("version").GetString(), draft.GetProperty("savedBy").GetString(), draft.GetProperty("path").GetString()));
                Assert.Contains("retry with includeDraft", refused.GetProperty("hint").GetString());
            }
            Assert.Equal(theirVersion, $"{intro}_{(await editor.OkAsync("get_content", new { reference = intro, version = "latest" })).GetProperty("version").GetInt32()}");

            // Confirmed: saved over their draft, and the result says whose it was.
            var saved = await editor.OkAsync("update_content", new { reference = page, properties = area, includeDraft = true });
            Assert.Equal("updated", Assert.Single(SharedBlocks(saved)).GetProperty("action").GetString());
            Assert.Contains(saved.GetProperty("validation").EnumerateArray(), v => v.GetProperty("severity").GetString() == "warning"
                && v.GetProperty("message").GetString()!.Contains($"over changes saved by {TestUsers.Admin} in {theirVersion}"));
            Assert.Contains("Editor's text", (await editor.OkAsync("get_content", new { reference = intro, version = "latest" })).GetProperty("properties").GetProperty("MainBody").GetRawText());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpInlineBlockModeFact(Shared)]
    public async Task Replacing_a_colleagues_block_draft_with_include_draft_doesnt_publish_their_draft_of_another_block_until_named()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var made = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, properties = new { MainContentArea = new[] { Block("Intro original text", "Intro") } } });
        var page = WritingTests.Id(made.GetProperty("content"));
        try
        {
            var intro = SharedBlocks(made)[0].GetProperty("ref").GetString()!.Split('_')[0];
            await editor.OkAsync("publish_content", new { reference = page });
            // A new block, never published, that the colleague then changes; and the colleague's change of the live block.
            var added = await editor.OkAsync("update_content", new { reference = page, areaOps = new[] { new { op = "add", property = "MainContentArea", type = "EditorialBlock", name = "Extra", values = new { MainBody = "<p>Extra text</p>" } } } });
            var extra = SharedBlocks(added)[0].GetProperty("ref").GetString()!.Split('_')[0];
            foreach (var (block, text) in new[] { (intro, "Colleague's intro text"), (extra, "Colleague's extra text") })
            {
                var latest = (await admin.OkAsync("get_content", new { reference = block, version = "latest" })).GetProperty("version").GetInt32();
                await admin.OkAsync("update_content", new { reference = block, properties = new { MainBody = $"<p>{text}</p>" }, baseVersion = latest });
            }
            var area = new { MainContentArea = new object[] { Block("Editor's text", "Intro"), new { @ref = extra } } };

            // The editor agreed to replace the colleague's draft of Intro (includeDraft) and publishes: Intro goes live as the
            // write's own version, the colleague's draft of Extra stays a draft, dry run and run alike, with a warning naming
            // who saved it and how to name it in blocks.
            JsonElement? result = null;
            foreach (var dryRun in new[] { true, false })
            {
                var shown = await editor.OkAsync("update_content", new { reference = page, name = ScratchName(), properties = area, includeDraft = true, publish = true, dryRun });
                Assert.Equal([intro], SharedBlocks(shown).Where(b => b.GetProperty("status").GetString() == "published").Select(b => b.GetProperty("ref").GetString()!.Split('_')[0]));
                var left = Assert.Single(shown.GetProperty("validation").EnumerateArray(), v => v.GetProperty("property").GetString() == "MainContentArea[1]").GetProperty("message").GetString()!;
                Assert.Contains($"('Extra') at MainContentArea[1] is a draft {TestUsers.Admin} saved, so it stays a draft", left);
                Assert.Contains($"blocks: [\"{extra}\"]", left);
                Assert.Equal("checkedOut", Status(await editor.OkAsync("get_content", new { reference = extra, version = "latest" })));
                result = shown;
            }
            Assert.Equal("published", Status(await editor.OkAsync("get_content", new { reference = intro, version = "latest" })));
            var html = await RenderAsync(result!.Value);
            Assert.Contains("Editor's text", html);
            Assert.DoesNotContain("Colleague's extra text", html);

            // Named in blocks, the colleague's draft goes live (the page is live already, so it alone).
            var named = await editor.OkAsync("publish_content", new { reference = page, blocks = new[] { extra } });
            Assert.Equal([extra], SharedBlocks(named).Select(b => b.GetProperty("ref").GetString()!.Split('_')[0]));
            Assert.Equal("published", Status(await editor.OkAsync("get_content", new { reference = extra, version = "latest" })));
            Assert.Contains("Colleague's extra text", await RenderAsync(named));
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
            // For the new block (Create on the page), not for the edit itself, which the product editor may make.
            Assert.Contains("You don't have Create access to", refused.GetProperty("message").GetString());
        }
    }

    [McpInlineBlockModeFact(Shared)]
    public async Task An_inline_block_a_page_already_has_stays_inline_and_an_editor_cant_drop_its_values_they_cant_change()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            // An inline block another tool put there (the edge-case fixture, through the CMS's API), with AdminScripts, which
            // is locked for all but administrators (McpFixture.cs).
            using (var http = new HttpClient { BaseAddress = McpSiteSettings.Url })
            using (var body = new StringContent("""{"Heading": "Inline", "AdminScripts": "admin only"}""", System.Text.Encoding.UTF8, "application/json"))
            using (var response = await http.PostAsync($"opticli-fixture/inline-block?content={page}&property=MainContentArea&type=McpFieldsBlock&name=Kept", body))
            {
                Assert.True(response.IsSuccessStatusCode, $"POST /opticli-fixture/inline-block answered {(int)response.StatusCode}: is the site built with this checkout's tests/fixtures/mcp/setup.sh?");
            }

            // Written whole with the block changed, it would stay inline but lose the value the editor can't see: refused.
            var area = InlineBlockTests.Writable(InlineBlockTests.Area(await editor.OkAsync("get_content", new { reference = page, version = "latest" })));
            area[0]!["properties"]!["Heading"] = "Changed whole";
            var refused = await editor.ErrorAsync("update_content", new { reference = page, properties = new Dictionary<string, System.Text.Json.Nodes.JsonNode> { ["MainContentArea"] = area }, dryRun = true });
            Assert.Equal("usage", refused.GetProperty("code").GetString());
            Assert.Contains("MainContentArea would lose MainContentArea[0].AdminScripts", refused.GetProperty("message").GetString());

            // Changed by its position, it stays inline and keeps the rest of its values; no shared block is made.
            var changed = await editor.OkAsync("update_content", new { reference = page, areaOps = new[] { new { op = "set", property = "MainContentArea", index = 0, values = new { Heading = "Changed" } } } });
            Assert.Empty(SharedBlocks(changed));
            var kept = InlineBlockTests.Area(await admin.OkAsync("get_content", new { reference = page, version = "latest" }))[0];
            Assert.True(kept.GetProperty("inline").GetBoolean());
            Assert.Equal(("Changed", "admin only"), (kept.GetProperty("properties").GetProperty("Heading").GetProperty("value").GetString(),
                kept.GetProperty("properties").GetProperty("AdminScripts").GetProperty("value").GetString()));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
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
