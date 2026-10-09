using System.Text.Json;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// What the CMS edit UI wouldn't let an editor do, and the CMS's own repository would: save script in rich text or a
/// link, read or change a property the edit UI hides or locks, change content in a language they may not edit. Each test
/// deletes the content it creates.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class EditUiRulesTests(SharedSessions sessions)
{
    private static string ScratchName() => $"opticli-mcp-it {Guid.NewGuid():N}";

    [McpSiteFact]
    public async Task Script_in_rich_text_and_links_is_refused_and_ordinary_rich_text_is_saved_as_given()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            foreach (var body in new[]
            {
                "<p>Hello</p><img src=x onerror=alert(document.domain)>",
                "<p><a href=\"jav&#x61;script:alert(1)\">Click</a></p>",
                "<script>fetch('/EPiServer/cms/Stores/contentdata/')</script>",
                "<iframe src=\"https://example.com\"></iframe>",
            })
            {
                foreach (var dryRun in new[] { true, false })
                {
                    var refused = await editor.ErrorAsync("update_content", new { reference = page, properties = new { MainBody = body }, dryRun });
                    Assert.Equal("usage", refused.GetProperty("code").GetString());
                    Assert.Contains("'MainBody' has", refused.GetProperty("message").GetString());
                    Assert.Contains("Nothing was saved", refused.GetProperty("hint").GetString());
                }
            }
            var shortcut = await editor.ErrorAsync("update_content", new { reference = page, properties = new { Shortcut = "javascript://%0aalert(1)" }, dryRun = true });
            Assert.Contains("javascript:", shortcut.GetProperty("message").GetString());
            var links = await editor.ErrorAsync("create_content", new
            {
                type = "McpFieldsBlock", name = ScratchName(), forContent = page, dryRun = true,
                properties = new { Links = new[] { new { href = "javascript:alert(1)", text = "x" } } },
            });
            Assert.Contains("'Links' has a javascript: link", links.GetProperty("message").GetString());
            var address = await editor.ErrorAsync("create_content", new { type = "McpFieldsBlock", name = ScratchName(), forContent = page, dryRun = true, properties = new { Address = "data:text/html,x" } });
            Assert.Contains("'Address' has a data: link", address.GetProperty("message").GetString());
            Assert.Single((await editor.OkAsync("list_versions", new { reference = page })).GetProperty("versions").EnumerateArray());

            const string ordinary = "<h2 class=\"lead\" style=\"color: #333;\">Heading</h2><p><a href=\"/en/about-us/\" title=\"About\">About us</a> and <a href=\"mailto:editor@example.com\">mail</a></p><table><tbody><tr><td>1 &lt; 2</td></tr></tbody></table>";
            await editor.OkAsync("update_content", new { reference = page, properties = new { MainBody = ordinary } });
            Assert.Equal(ordinary, WritingTests.Property(await editor.OkAsync("get_content", new { reference = page, version = "latest" }), "MainBody"));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task Properties_the_edit_UI_hides_or_locks_are_neither_read_nor_written_for_an_editor()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        // McpFieldsBlock (McpFixture.cs): Hidden is [ScaffoldColumn(false)], Locked [Editable(false)], AdminScripts locked
        // for all but WebAdmins by an editor descriptor, AdminTab on a tab that needs Administer.
        var block = WritingTests.Id((await editor.OkAsync("create_content", new { type = "McpFieldsBlock", name = ScratchName(), forContent = start, properties = new { Heading = "Shown" } })).GetProperty("content"));
        try
        {
            var asEditor = Names((await editor.OkAsync("get_content", new { reference = block, version = "latest" })).GetProperty("properties"));
            Assert.Contains("Heading", asEditor);
            Assert.Contains("Body", asEditor);
            Assert.DoesNotContain("Hidden", asEditor);
            Assert.DoesNotContain("Locked", asEditor);
            Assert.DoesNotContain("AdminScripts", asEditor);
            Assert.DoesNotContain("AdminTab", asEditor);
            var asAdmin = Names((await admin.OkAsync("get_content", new { reference = block, version = "latest" })).GetProperty("properties"));
            Assert.Contains("AdminScripts", asAdmin);
            Assert.Contains("AdminTab", asAdmin);
            Assert.DoesNotContain("Hidden", asAdmin);
            Assert.DoesNotContain("Locked", asAdmin);
            var type = (await editor.OkAsync("get_content_type", new { name = "McpFieldsBlock" })).GetProperty("properties").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
            Assert.Contains("Heading", type);
            Assert.DoesNotContain("Hidden", type);
            Assert.DoesNotContain("Locked", type);

            foreach (var property in new[] { "Hidden", "Locked", "AdminScripts", "AdminTab" })
            {
                var refused = await editor.ErrorAsync("update_content", new { reference = block, properties = new Dictionary<string, string> { [property] = "x" }, dryRun = true });
                Assert.Equal("usage", refused.GetProperty("code").GetString());
                Assert.Contains("not editable in the CMS edit UI for you", refused.GetProperty("message").GetString());
            }
            await admin.OkAsync("update_content", new { reference = block, properties = new { AdminScripts = "x", AdminTab = "y" }, dryRun = true });
            var hidden = await admin.ErrorAsync("update_content", new { reference = block, properties = new { Hidden = "x" }, dryRun = true });
            Assert.Contains("not editable in the CMS edit UI for you", hidden.GetProperty("message").GetString());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, block);
        }
    }

    [McpSiteFact]
    public async Task A_language_only_administrators_may_edit_is_refused_to_an_editor()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            foreach (var dryRun in new[] { true, false })
            {
                var refused = await editor.ErrorAsync("add_language", new { reference = page, lang = TestUsers.AdminLanguage, dryRun });
                Assert.Equal("refused", refused.GetProperty("code").GetString());
                Assert.Contains($"'{TestUsers.AdminLanguage}'", refused.GetProperty("message").GetString());
            }
            Assert.Equal("refused", (await editor.ErrorAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, lang = TestUsers.AdminLanguage, dryRun = true })).GetProperty("code").GetString());

            await admin.OkAsync("add_language", new { reference = page, lang = TestUsers.AdminLanguage, name = "Seite" });
            var update = await editor.ErrorAsync("update_content", new { reference = page, lang = TestUsers.AdminLanguage, name = "Geändert", dryRun = true });
            Assert.Equal("refused", update.GetProperty("code").GetString());
            await admin.OkAsync("update_content", new { reference = page, lang = TestUsers.AdminLanguage, name = "Geändert" });
            // Any other language is the editor's to translate, as before.
            await editor.OkAsync("add_language", new { reference = page, lang = "sv", dryRun = true });
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task Links_given_as_markup_are_refused_as_every_attribute_in_them_would_be_rendered()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));

        var refused = await editor.ErrorAsync("create_content", new
        {
            type = "McpFieldsBlock", name = ScratchName(), forContent = start,
            properties = new { Links = "<links><a href=\"/en/\" onclick=\"alert(document.domain)\" onmouseover=\"alert(2)\">x</a></links>" },
        });

        Assert.Equal("usage", refused.GetProperty("code").GetString());
        Assert.Contains("takes links as objects", refused.GetProperty("message").GetString());
    }

    [McpSiteFact]
    public async Task What_a_property_already_has_may_be_written_back_as_it_is_but_nothing_new()
    {
        // The fixture's block has a video's <iframe> in Body and an sms: link, which the edit UI could have saved.
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var stored = await editor.OkAsync("get_content", new { reference = TestUsers.WriteBackBlock });
        var block = WritingTests.Id(stored);
        var body = WritingTests.Property(stored, "Body")!;
        var links = stored.GetProperty("properties").GetProperty("Links").GetProperty("value");
        Assert.Contains("<iframe", body);

        var fixedTypo = await editor.OkAsync("update_content", new { reference = block, properties = new { Body = body.Replace("teh", "the"), Links = links }, dryRun = true });
        Assert.True(fixedTypo.GetProperty("valid").GetBoolean());

        var iframe = body[body.IndexOf("<iframe", StringComparison.Ordinal)..];
        foreach (var changed in new[] { body + iframe, body.Replace("embed/opticli", "embed/other") })
        {
            var refused = await editor.ErrorAsync("update_content", new { reference = block, properties = new { Body = changed }, dryRun = true });
            Assert.Contains("<iframe> element", refused.GetProperty("message").GetString());
        }
        var moreLinks = await editor.ErrorAsync("update_content", new
        {
            reference = block, dryRun = true,
            properties = new { Links = new[] { new { href = "sms:+4712345678", text = "Text us" }, new { href = "sms:+4700000000", text = "Or us" } } },
        });
        Assert.Contains("sms:", moreLinks.GetProperty("message").GetString());
    }

    [McpSiteFact]
    public async Task Script_a_browser_reads_as_text_in_the_stored_value_isnt_written_back_live()
    {
        // The fixture's Body has <img onerror> inside a <textarea>: inert there.
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var stored = await editor.OkAsync("get_content", new { reference = TestUsers.InertBlock });
        var block = WritingTests.Id(stored);
        Assert.Contains("<textarea><img", WritingTests.Property(stored, "Body"));

        var live = await editor.ErrorAsync("update_content", new { reference = block, properties = new { Body = "<p>hi</p><img src=x onerror=alert(document.domain)>" }, dryRun = true });
        Assert.Contains("'onerror'", live.GetProperty("message").GetString());
    }

    [McpSiteFact]
    public async Task A_link_with_a_control_character_is_refused_before_anything_is_saved()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();

        foreach (var (href, dryRun) in new[] { ("javascrip\u0001t:alert(1)", true), ("/en/\u0000x", false) })
        {
            var refused = await editor.ErrorAsync("create_content", new { type = "McpFieldsBlock", name, forContent = start, dryRun, properties = new { Links = new[] { new { href, text = "x" } } } });
            Assert.Equal("usage", refused.GetProperty("code").GetString());
            Assert.Contains("control character", refused.GetProperty("message").GetString());
        }
        Assert.Empty((await editor.OkAsync("find_content", new { name, root = start })).GetProperty("items").EnumerateArray());
    }

    [McpSiteFact]
    public async Task An_svg_with_script_and_files_a_browser_runs_script_in_are_not_uploaded()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        string Data(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

        var script = await editor.ErrorAsync("upload_media", new { fileName = "logo.svg", forContent = start, dryRun = true,
            data = Data("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(document.domain)</script><rect width=\"10\" height=\"10\"/></svg>") });
        Assert.Contains("The SVG file has a <script> element", script.GetProperty("message").GetString());
        var html = await editor.ErrorAsync("upload_media", new { fileName = "page.html", forContent = start, dryRun = true, data = Data("<p>x</p>") });
        Assert.Contains(".html files can run script", html.GetProperty("message").GetString());
        var disguised = await editor.ErrorAsync("upload_media", new { fileName = "photo.html.jpg", forContent = start, dryRun = true, data = Data("<html><script>alert(1)</script></html>") });
        Assert.Contains("starts like markup", disguised.GetProperty("message").GetString());

        var drawing = await editor.OkAsync("upload_media", new { fileName = "logo.svg", forContent = start, dryRun = true,
            data = Data("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"#c00\"/></svg>") });
        Assert.True(drawing.GetProperty("valid").GetBoolean());
    }

    [McpSiteFact]
    public async Task What_the_edit_UI_locks_inside_a_local_block_or_a_block_list_is_locked_and_kept()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        // McpInnerBlock.AdminOnly: locked for all but WebAdmins by an editor descriptor.
        var block = WritingTests.Id((await editor.OkAsync("create_content", new { type = "McpFieldsBlock", name = ScratchName(), forContent = start, properties = new { Heading = "Inner" } })).GetProperty("content"));
        try
        {
            await admin.OkAsync("update_content", new
            {
                reference = block,
                properties = new { Inner = new { Title = "Local", AdminOnly = "admin-local" }, Items = new[] { new { Title = "Item", AdminOnly = "admin-item" } } },
            });

            var read = await editor.OkAsync("get_content", new { reference = block, version = "latest" });
            Assert.DoesNotContain("admin-local", read.GetRawText());
            Assert.DoesNotContain("admin-item", read.GetRawText());
            Assert.Contains("Local", read.GetProperty("properties").GetProperty("Inner").GetRawText());
            Assert.Contains("not editable in the CMS edit UI for you",
                (await editor.ErrorAsync("update_content", new { reference = block, properties = new { Inner = new { AdminOnly = "x" } }, dryRun = true })).GetProperty("message").GetString());
            await admin.OkAsync("update_content", new { reference = block, properties = new { Inner = new { AdminOnly = "y" } }, dryRun = true });

            // A diff shows the block whole, but never what the editor can't see of it.
            var diff = await editor.OkAsync("update_content", new { reference = block, properties = new { Inner = new { Title = "Renamed" } }, dryRun = true });
            Assert.Contains("Renamed", diff.GetProperty("changes").GetRawText());
            Assert.DoesNotContain("admin-local", diff.GetRawText());

            // Rewriting the list keeps what the editor can't see of its items, and can't drop it.
            await editor.OkAsync("update_content", new { reference = block, properties = new { Items = new[] { new { Title = "Item renamed" } } } });
            var items = (await admin.OkAsync("get_content", new { reference = block, version = "latest" })).GetProperty("properties").GetProperty("Items").GetRawText();
            Assert.Contains("Item renamed", items);
            Assert.Contains("admin-item", items);
            Assert.Contains("would lose items",
                (await editor.ErrorAsync("update_content", new { reference = block, properties = new { Items = Array.Empty<object>() }, dryRun = true })).GetProperty("message").GetString());

            // get_content's own shape written back is a usage error, not a failure of the site.
            var shape = await editor.ErrorAsync("update_content", new { reference = block, properties = new { Inner = new { Title = new { type = "LongString", value = "x" } } }, dryRun = true });
            Assert.Equal("usage", shape.GetProperty("code").GetString());
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, block);
        }
    }

    [McpSiteFact]
    public async Task A_tab_that_needs_Administer_hides_a_block_lists_items_values_from_an_editor_but_not_a_local_blocks()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        // McpInnerBlock.AdminTab is on a tab that needs Administer, which the editor hasn't got on the block. The edit UI
        // hides it in the block list's items (each item's form is made as a new block below the content) but shows it in
        // the local block, whose form has no access rights of its own.
        var block = WritingTests.Id((await editor.OkAsync("create_content", new { type = "McpFieldsBlock", name = ScratchName(), forContent = start, properties = new { Heading = "Tabs" } })).GetProperty("content"));
        try
        {
            await admin.OkAsync("update_content", new
            {
                reference = block,
                properties = new { Inner = new { Title = "Local", AdminTab = "local-tab" }, Items = new[] { new { Title = "Item", AdminTab = "item-tab" } } },
            });

            var read = await editor.OkAsync("get_content", new { reference = block, version = "latest" });
            Assert.Contains("local-tab", read.GetProperty("properties").GetProperty("Inner").GetRawText());
            Assert.DoesNotContain("item-tab", read.GetRawText());
            Assert.Contains("item-tab", (await admin.OkAsync("get_content", new { reference = block, version = "latest" })).GetRawText());

            await editor.OkAsync("update_content", new { reference = block, properties = new { Inner = new { AdminTab = "x" } }, dryRun = true });
            Assert.Contains("'AdminTab' is not editable in the CMS edit UI for you",
                (await editor.ErrorAsync("update_content", new { reference = block, properties = new { Items = new[] { new { Title = "Item", AdminTab = "x" } } }, dryRun = true })).GetProperty("message").GetString());
            await admin.OkAsync("update_content", new { reference = block, properties = new { Items = new[] { new { Title = "Item", AdminTab = "x" } } }, dryRun = true });

            // The list rewritten keeps the value, and its diff doesn't show it.
            var diff = await editor.OkAsync("update_content", new { reference = block, properties = new { Items = new[] { new { Title = "Item renamed" } } }, dryRun = true });
            Assert.Contains("Item renamed", diff.GetProperty("changes").GetRawText());
            Assert.DoesNotContain("item-tab", diff.GetRawText());
            await editor.OkAsync("update_content", new { reference = block, properties = new { Items = new[] { new { Title = "Item renamed" } } } });
            var items = (await admin.OkAsync("get_content", new { reference = block, version = "latest" })).GetProperty("properties").GetProperty("Items").GetRawText();
            Assert.Contains("Item renamed", items);
            Assert.Contains("item-tab", items);
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, block);
        }
    }

    private static List<string> Names(JsonElement properties) => [.. properties.EnumerateObject().Select(p => p.Name)];
}
