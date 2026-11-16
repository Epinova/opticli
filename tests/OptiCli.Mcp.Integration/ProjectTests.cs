using System.Text.Json;
using OptiCli.Mcp.Integration.Support;
using static OptiCli.Mcp.Integration.WritingTests;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// CMS projects as the editor: a project made, drafts saved into it (every save tool), items marked ready, taken out,
/// and the project published with only its ready items going live, or scheduled and reactivated; an editor without
/// Publish rights, one who can't read an item, approval sequences, someone else's draft, a connection without the
/// publish scope, and a site that doesn't let assistants publish. The test site runs the CMS's defaults: project mode on
/// CMS 12, only the Projects gadget on CMS 13. Each test moves its pages to the recycle bin; its projects stay (the
/// module can't delete one), named <c>opticli-mcp-it …</c>.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class ProjectTests(SharedSessions sessions)
{
    /// <summary>A 1×1 PNG.</summary>
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static string ScratchName() => $"opticli-mcp-it {Guid.NewGuid():N}";

    /// <summary>
    /// The project UI the test site's CMS gives editors: its default (project mode on CMS 12, the gadget on 13), or what
    /// <c>OPTICLI_MCP_IT_EDITOR_UI</c> says for a site started otherwise (a CMS 13 site with
    /// <c>EPiServer__CmsUI__ProjectUI__ProjectModeEnabled=true</c> in serve.sh's environment: <c>projectMode</c>).
    /// </summary>
    private static string EditorUi =>
        Environment.GetEnvironmentVariable("OPTICLI_MCP_IT_EDITOR_UI") is { Length: > 0 } ui ? ui : McpSiteSettings.CmsMajor >= 13 ? "gadget" : "projectMode";

    [McpSiteFact]
    public async Task A_campaign_goes_into_a_project_and_publishing_it_puts_only_the_ready_items_live()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();

        var dryRun = await editor.OkAsync("create_project", new { name, dryRun = true });
        Assert.Equal(0, dryRun.GetProperty("project").GetProperty("id").GetInt32());
        Assert.False(dryRun.TryGetProperty("projectUrl", out _));
        var made = await editor.OkAsync("create_project", new { name });
        var project = made.GetProperty("project").GetProperty("id").GetInt32();
        Assert.True(made.GetProperty("created").GetBoolean());
        Assert.Equal((TestUsers.Editor, EditorUi), (made.GetProperty("project").GetProperty("createdBy").GetString(), made.GetProperty("editorUi").GetString()));
        ProjectLink(made, project);
        // A second project of that name is refused: projects are named by their name too.
        Assert.Equal("conflict", (await editor.ErrorAsync("create_project", new { name })).GetProperty("code").GetString());

        var pages = new List<string>();
        try
        {
            // A page made into the project, ready; its dry run says what it would do.
            var check = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = name, ready = true, dryRun = true });
            Assert.Equal(("added", true), (check.GetProperty("project").GetProperty("item").GetString(), check.GetProperty("project").GetProperty("ready").GetBoolean()));
            var ready = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = name, ready = true });
            var readyPage = Id(ready.GetProperty("content"));
            pages.Add(readyPage);
            Assert.Equal((project, "added", true), SavedInto(ready));
            Assert.Equal("checkedIn", ready.GetProperty("content").GetProperty("status").GetString());
            ProjectLink(ready.GetProperty("project"), project);
            EditUrl(ready, ready.GetProperty("content").GetProperty("ref").GetString()!);

            // A page made into it, not ready, then changed: its new draft takes the old one's place.
            var draft = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = project.ToString() });
            var draftPage = Id(draft.GetProperty("content"));
            pages.Add(draftPage);
            Assert.Equal((project, "added", false), SavedInto(draft));
            var changed = await editor.OkAsync("update_content", new { reference = draftPage, properties = new { TeaserText = "changed" }, project = name });
            Assert.Equal((project, "replaced", false), SavedInto(changed));
            Assert.Equal(draft.GetProperty("content").GetProperty("ref").GetString(), changed.GetProperty("project").GetProperty("replaced").GetString());

            // Its Swedish branch and a picture for it go in too.
            var swedish = await editor.OkAsync("add_language", new { reference = draftPage, lang = "sv", project = name });
            Assert.Equal((project, "added", false), SavedInto(swedish));
            var picture = await editor.OkAsync("upload_media", new { fileName = "pixel.png", data = Pixel, forContent = draftPage, project = name, ready = true });
            Assert.Equal((project, "added", true), SavedInto(picture));
            var media = Id(picture.GetProperty("content"));

            // The page reads as being in the project; the project lists its items, each with its state and edit link.
            var held = Assert.Single((await editor.OkAsync("get_content", new { reference = readyPage, version = "latest" })).GetProperty("projects").EnumerateArray());
            Assert.Equal((project, name, "active", "en"), (held.GetProperty("id").GetInt32(), held.GetProperty("name").GetString(), held.GetProperty("status").GetString(), held.GetProperty("language").GetString()));
            var read = await editor.OkAsync("get_project", new { project = name });
            ProjectLink(read, project);
            Assert.Equal((4, 2), (read.GetProperty("project").GetProperty("items").GetInt32(), read.GetProperty("project").GetProperty("ready").GetInt32()));
            var states = read.GetProperty("items").EnumerateArray().ToDictionary(i => $"{Id(i.GetProperty("content"))} {Language(i.GetProperty("content"))}", i => i.GetProperty("state").GetString());
            Assert.Equal("ready", states[$"{readyPage} en"]);
            Assert.Equal("draft", states[$"{draftPage} en"]);
            Assert.Equal("draft", states[$"{draftPage} sv"]);
            Assert.Equal("ready", Assert.Single(states, s => s.Key.StartsWith(media + " ", StringComparison.Ordinal)).Value);
            Assert.All(read.GetProperty("items").EnumerateArray(), i => EditUrl(i, i.GetProperty("content").GetProperty("ref").GetString()!));
            var listed = Assert.Single((await editor.OkAsync("list_projects")).GetProperty("projects").EnumerateArray(), p => p.GetProperty("id").GetInt32() == project);
            Assert.Equal(2, listed.GetProperty("ready").GetInt32());

            // Content in this project isn't put in another one.
            var other = await editor.OkAsync("create_project", new { name = ScratchName() });
            var refused = await editor.ErrorAsync("update_content", new { reference = readyPage, properties = new { TeaserText = "elsewhere" }, project = other.GetProperty("project").GetProperty("id").GetInt32().ToString() });
            Assert.Equal(("conflict", "inOtherProject"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
            Assert.Contains("remove_from_project", refused.GetProperty("hint").GetString());

            // The Swedish branch is taken out: its draft stays.
            var removeCheck = await editor.OkAsync("remove_from_project", new { project = name, items = new[] { draftPage }, lang = "sv", dryRun = true });
            Assert.Equal("removed", Assert.Single(removeCheck.GetProperty("items").EnumerateArray()).GetProperty("outcome").GetString());
            await editor.OkAsync("remove_from_project", new { project = name, items = new[] { draftPage }, lang = "sv" });
            Assert.Equal(3, (await editor.OkAsync("get_project", new { project = name })).GetProperty("items").GetArrayLength());
            Assert.Contains((await editor.OkAsync("list_versions", new { reference = draftPage, lang = "sv" })).GetProperty("versions").EnumerateArray(), v => v.GetProperty("status").GetString() == "checkedOut");

            // Publishing: the dry run says what goes live and what stays behind; then only that goes live.
            var preview = await editor.OkAsync("publish_project", new { project = name, dryRun = true });
            Assert.Equal([readyPage, media], preview.GetProperty("published").EnumerateArray().Select(i => Id(i.GetProperty("content"))).Order());
            Assert.Equal(draftPage, Id(Assert.Single(preview.GetProperty("notReady").EnumerateArray()).GetProperty("content")));
            Assert.Equal("active", (await editor.OkAsync("get_project", new { project = name })).GetProperty("project").GetProperty("status").GetString());
            var published = await editor.OkAsync("publish_project", new { project = name });
            Assert.Equal("published", published.GetProperty("project").GetProperty("status").GetString());
            var live = published.GetProperty("published").EnumerateArray().Single(i => Id(i.GetProperty("content")) == readyPage);
            Assert.Equal(readyPage, Id((await editor.OkAsync("resolve_url", new { url = live.GetProperty("url").GetString() })).GetProperty("content")));
            Assert.Equal("published", (await editor.OkAsync("get_content", new { reference = readyPage })).GetProperty("status").GetString());
            Assert.Equal("checkedOut", (await editor.OkAsync("get_content", new { reference = draftPage, version = "latest" })).GetProperty("status").GetString());

            // A published project takes no more saves.
            var closed = await editor.ErrorAsync("update_content", new { reference = draftPage, properties = new { TeaserText = "late" }, project = name });
            Assert.Equal(("conflict", "projectNotActive"), (closed.GetProperty("code").GetString(), closed.GetProperty("reason").GetString()));
            Assert.Contains("create_project", closed.GetProperty("hint").GetString());
        }
        finally
        {
            foreach (var page in pages)
            {
                await DeleteAsync(editor, page);
            }
        }
    }

    [McpSiteFact]
    public async Task A_scheduled_project_is_reactivated_and_its_items_are_drafts_again()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        var project = (await editor.OkAsync("create_project", new { name })).GetProperty("project").GetProperty("id").GetInt32();
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = name })).GetProperty("content"));
        try
        {
            // Nothing ready: nothing to publish.
            var nothing = await editor.ErrorAsync("publish_project", new { project = name });
            Assert.Equal(("conflict", "nothingReady"), (nothing.GetProperty("code").GetString(), nothing.GetProperty("reason").GetString()));
            Assert.Contains("mark_ready_to_publish", nothing.GetProperty("hint").GetString());

            var marked = await editor.OkAsync("mark_ready_to_publish", new { project = name, all = true });
            Assert.Equal("ready", Assert.Single(marked.GetProperty("items").EnumerateArray()).GetProperty("outcome").GetString());
            ProjectLink(marked, project);

            var at = DateTimeOffset.Now.AddDays(7);
            var scheduled = await editor.OkAsync("publish_project", new { project = project.ToString(), publishAt = at });
            Assert.Equal("delayedPublished", scheduled.GetProperty("project").GetProperty("status").GetString());
            Assert.Equal(page, Id(Assert.Single(scheduled.GetProperty("scheduled").EnumerateArray()).GetProperty("content")));
            var waiting = await editor.ErrorAsync("update_content", new { reference = page, properties = new { TeaserText = "late" }, project = name });
            Assert.Equal("projectNotActive", waiting.GetProperty("reason").GetString());
            Assert.Contains("reactivate_project", waiting.GetProperty("hint").GetString());

            await editor.OkAsync("reactivate_project", new { project = name, dryRun = true });
            Assert.Equal("delayedPublished", (await editor.OkAsync("get_project", new { project = name })).GetProperty("project").GetProperty("status").GetString());
            var reactivated = await editor.OkAsync("reactivate_project", new { project = name });
            Assert.True(reactivated.GetProperty("reactivated").GetBoolean());
            Assert.Equal("active", reactivated.GetProperty("project").GetProperty("status").GetString());
            Assert.Equal("draft", Assert.Single((await editor.OkAsync("get_project", new { project = name })).GetProperty("items").EnumerateArray()).GetProperty("state").GetString());
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task Someone_elses_changes_in_a_ready_item_go_live_only_with_includeDraft()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, publish = true })).GetProperty("content"));
        var name = ScratchName();
        await editor.OkAsync("create_project", new { name });
        try
        {
            await admin.OkAsync("update_content", new { reference = page, name = "Saved by a colleague" });
            await editor.OkAsync("update_content", new { reference = page, properties = new { TeaserText = "for the campaign" }, project = name, ready = true });

            var preview = await editor.OkAsync("publish_project", new { project = name, dryRun = true });
            Assert.Equal(TestUsers.Admin, Assert.Single(preview.GetProperty("published").EnumerateArray()).GetProperty("pendingDraft").GetProperty("savedBy").GetString());
            var refused = await editor.ErrorAsync("publish_project", new { project = name });
            Assert.Equal(("conflict", "pendingDraft"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
            Assert.Equal(TestUsers.Admin, refused.GetProperty("pendingDraft").GetProperty("savedBy").GetString());
            Assert.NotEqual("Saved by a colleague", (await editor.OkAsync("get_content", new { reference = page })).GetProperty("name").GetString());

            var published = await editor.OkAsync("publish_project", new { project = name, includeDraft = true });
            Assert.Equal("published", published.GetProperty("project").GetProperty("status").GetString());
            Assert.Equal("Saved by a colleague", (await editor.OkAsync("get_content", new { reference = page })).GetProperty("name").GetString());
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task Ready_under_an_approval_sequence_is_refused_and_a_review_request_keeps_the_draft_in_the_project()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var root = Id(await editor.OkAsync("get_content", new { reference = TestUsers.ApprovalRoot }));
        var name = ScratchName();
        await editor.OkAsync("create_project", new { name });
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = root, project = name })).GetProperty("content"));
        try
        {
            var refused = await editor.ErrorAsync("update_content", new { reference = page, properties = new { MetaTitle = "x" }, project = name, ready = true });
            Assert.Equal(("refused", "approvalSequence"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
            var markRefused = await editor.ErrorAsync("mark_ready_to_publish", new { project = name, items = new[] { page } });
            Assert.Equal("approvalSequence", markRefused.GetProperty("reason").GetString());
            Assert.Contains("requestApproval", markRefused.GetProperty("hint").GetString());

            var review = await editor.OkAsync("mark_ready_to_publish", new { project = name, items = new[] { page }, requestApproval = true, comment = "Spring campaign: please check" });
            Assert.Equal("approvalRequested", Assert.Single(review.GetProperty("items").EnumerateArray()).GetProperty("outcome").GetString());
            var item = Assert.Single((await editor.OkAsync("get_project", new { project = name })).GetProperty("items").EnumerateArray());
            Assert.Equal("inReview", item.GetProperty("state").GetString());
            Assert.Equal(("conflict", "nothingReady"), Code(await editor.ErrorAsync("publish_project", new { project = name })));
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task An_editor_marks_ready_and_takes_out_only_what_they_may_edit_and_publishes_only_what_they_may_publish()
    {
        var product = await sessions.ForAsync(TestUsers.Product);
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var hidden = Id(await product.OkAsync("get_content", new { reference = TestUsers.HiddenPage }));
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        var project = (await product.OkAsync("create_project", new { name })).GetProperty("project").GetProperty("id").GetInt32();
        int? draftVersion = null;
        string? page = null;
        try
        {
            // Product editors may edit the hidden page, not publish it: a draft of it goes in, ready (Edit is enough).
            var saved = await product.OkAsync("update_content", new { reference = hidden, name = $"Alloy Meet ({Guid.NewGuid():N})", project = name, ready = true });
            draftVersion = saved.GetProperty("content").GetProperty("version").GetInt32();
            Assert.Equal((project, "added", true), SavedInto(saved));

            // The editor can't read the hidden page: it isn't in the project for them, and they can't publish the project.
            var seen = await editor.OkAsync("get_project", new { project = name });
            Assert.Empty(seen.GetProperty("items").EnumerateArray());
            Assert.Equal((0, 0), (seen.GetProperty("project").GetProperty("items").GetInt32(), seen.GetProperty("project").GetProperty("ready").GetInt32()));
            Assert.DoesNotContain("Alloy Meet", seen.GetRawText());
            Assert.Equal("not_found", (await editor.ErrorAsync("remove_from_project", new { project = name, items = new[] { hidden } })).GetProperty("code").GetString());
            var blind = await editor.ErrorAsync("publish_project", new { project = name });
            Assert.Equal("refused", blind.GetProperty("code").GetString());
            Assert.Contains("can't read", blind.GetProperty("message").GetString());
            Assert.DoesNotContain("Alloy Meet", blind.GetRawText());

            // A page the editor makes into the project: product editors can read it, not edit it.
            page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = name })).GetProperty("content"));
            var cantMark = await product.ErrorAsync("mark_ready_to_publish", new { project = name, items = new[] { page } });
            Assert.Equal("refused", cantMark.GetProperty("code").GetString());
            Assert.Contains("Edit access", cantMark.GetProperty("message").GetString());
            // Its dry run says so, as mark_ready_to_publish's does; the call itself is refused.
            var removeCheck = await product.OkAsync("remove_from_project", new { project = name, items = new[] { page }, dryRun = true });
            Assert.False(removeCheck.GetProperty("valid").GetBoolean());
            Assert.Equal("refused", Assert.Single(removeCheck.GetProperty("items").EnumerateArray()).GetProperty("problem").GetProperty("code").GetString());
            var cantRemove = await product.ErrorAsync("remove_from_project", new { project = name, items = new[] { page } });
            Assert.Equal("refused", cantRemove.GetProperty("code").GetString());
            Assert.Contains("Edit access", cantRemove.GetProperty("message").GetString());
            Assert.Equal(2, (await product.OkAsync("get_project", new { project = name })).GetProperty("items").GetArrayLength());

            // Publishing needs Publish on every ready item: product editors haven't it on the hidden page.
            var publishCheck = await product.OkAsync("publish_project", new { project = name, dryRun = true });
            Assert.False(publishCheck.GetProperty("valid").GetBoolean());
            Assert.Contains("Publish access", Assert.Single(publishCheck.GetProperty("published").EnumerateArray()).GetProperty("problem").GetProperty("message").GetString());
            var cantPublish = await product.ErrorAsync("publish_project", new { project = name });
            Assert.Equal("refused", cantPublish.GetProperty("code").GetString());
            Assert.Contains("Publish access", cantPublish.GetRawText());
            Assert.Equal("published", (await product.OkAsync("get_content", new { reference = hidden })).GetProperty("status").GetString());

            // Taking out what they may edit works.
            await product.OkAsync("remove_from_project", new { project = name, items = new[] { hidden } });
            Assert.Equal(page, Id(Assert.Single((await product.OkAsync("get_project", new { project = name })).GetProperty("items").EnumerateArray()).GetProperty("content")));
        }
        finally
        {
            if (draftVersion is { } version)
            {
                // Out of the project (a no-op once taken out above), then thrown away by an administrator.
                await admin.CallAsync("remove_from_project", new { project = name, items = new[] { $"{hidden}_{version}" } });
                await admin.OkAsync("discard_draft", new { reference = $"{hidden}_{version}", includeDraft = true });
            }
            if (page is not null)
            {
                await DeleteAsync(editor, page);
            }
        }
    }

    [McpSiteFact]
    public async Task A_connection_without_the_publish_scope_makes_and_fills_projects_but_publishes_none()
    {
        await using var writer = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Allow = ["content:write"] });
        var editor = await sessions.ForAsync(TestUsers.Editor);
        Assert.Equal(["content:read", "content:write"], SignInTests.Strings((await writer.OkAsync("whoami")).GetProperty("scopes")));
        var start = Id((await writer.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        await writer.OkAsync("create_project", new { name });
        var page = Id((await writer.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = name, ready = true })).GetProperty("content"));
        try
        {
            foreach (var (tool, arguments) in new (string, object)[]
            {
                ("publish_project", new { project = name }),
                ("publish_project", new { project = name, dryRun = true }),
                ("reactivate_project", new { project = name }),
            })
            {
                var refused = await writer.ErrorAsync(tool, arguments);
                Assert.Equal(("refused", "missingScope"), Code(refused));
            }
            Assert.Equal("active", (await writer.OkAsync("get_project", new { project = name })).GetProperty("project").GetProperty("status").GetString());
            // Nor does a save into the project, whatever it asks.
            Assert.Equal(("refused", "missingScope"), Code(await writer.ErrorAsync("update_content", new { reference = page, project = name, publish = true })));
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    [NoPublishSiteFact]
    public async Task A_site_that_does_not_allow_publishing_offers_projects_but_not_publishing_them()
    {
        var site = McpSiteSettings.NoPublishUrl!;
        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = site });
        var tools = (await editor.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("create_project", tools);
        Assert.Contains("mark_ready_to_publish", tools);
        Assert.Contains("remove_from_project", tools);
        Assert.Contains("list_projects", tools);
        Assert.DoesNotContain("publish_project", tools);
        Assert.DoesNotContain("reactivate_project", tools);
        Assert.Contains("This site doesn't let assistants publish projects", editor.Client.ServerInstructions);

        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        await editor.OkAsync("create_project", new { name });
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = name })).GetProperty("content"));
        try
        {
            var marked = await editor.OkAsync("mark_ready_to_publish", new { project = name, items = new[] { page } });
            Assert.Equal("ready", Assert.Single(marked.GetProperty("items").EnumerateArray()).GetProperty("outcome").GetString());
            // Not there to call.
            var unknown = await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(() => editor.CallAsync("publish_project", new { project = name }));
            Assert.Contains("Unknown tool", unknown.Message);
            Assert.Equal("ready", Assert.Single((await editor.OkAsync("get_project", new { project = name })).GetProperty("items").EnumerateArray()).GetProperty("state").GetString());
            Assert.Equal(("refused", "publishingOff"), Code(await editor.ErrorAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, project = name, publish = true })));
        }
        finally
        {
            await DeleteAsync(await sessions.ForAsync(TestUsers.Editor), page);
        }
    }

    [McpSiteFact]
    public async Task A_site_write_tools_plan_saves_its_drafts_into_a_project_and_leaves_its_folder_out()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        var project = (await editor.OkAsync("create_project", new { name })).GetProperty("project").GetProperty("id").GetInt32();
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            var arguments = new Dictionary<string, object?> { ["page"] = int.Parse(page), ["name"] = ScratchName(), ["project"] = name, ["ready"] = true };
            var dry = await editor.OkAsync("fixture_build_section", new Dictionary<string, object?>(arguments) { ["dryRun"] = true });
            Assert.True(dry.GetProperty("valid").GetBoolean(), dry.GetRawText());
            var planned = dry.GetProperty("steps").EnumerateArray().ToList();
            // The section, the block, the page's update and the child go in, ready; the folder stays out.
            Assert.Equal([true, false, true, true, true], planned.Select(s => s.TryGetProperty("project", out _)));
            Assert.All(planned.Where(s => s.TryGetProperty("project", out _)), s => Assert.True(s.GetProperty("project").GetProperty("ready").GetBoolean()));

            var ran = await editor.OkAsync("fixture_build_section", new Dictionary<string, object?>(arguments) { ["planToken"] = dry.GetProperty("planToken").GetString() });
            var steps = ran.GetProperty("steps").EnumerateArray().ToList();
            Assert.All(steps, s => Assert.Equal("saved", s.GetProperty("status").GetString()));
            Assert.All(steps.Where(s => s.TryGetProperty("project", out _)), s => Assert.Equal((project, true), (s.GetProperty("project").GetProperty("id").GetInt32(), s.GetProperty("project").GetProperty("ready").GetBoolean())));
            var items = (await editor.OkAsync("get_project", new { project = name })).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(4, items.Count);
            Assert.All(items, i => Assert.Equal("ready", i.GetProperty("state").GetString()));
            Assert.Contains(items, i => Id(i.GetProperty("content")) == page);
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    /// <summary>What a save did to its project: id, item, ready.</summary>
    private static (int Id, string? Item, bool Ready) SavedInto(JsonElement result)
    {
        var project = result.GetProperty("project");
        Assert.False(project.TryGetProperty("error", out _), project.GetRawText());
        return (project.GetProperty("id").GetInt32(), project.GetProperty("item").GetString(), project.GetProperty("ready").GetBoolean());
    }

    private static string Language(JsonElement content) =>
        content.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString()! : "-";

    private static (string? Code, string? Reason) Code(JsonElement error) =>
        (error.GetProperty("code").GetString(), error.TryGetProperty("reason", out var reason) ? reason.GetString() : null);

    /// <summary>
    /// Where the result says the project is: in project mode (CMS 12's default) the edit UI's link to it, on the site the
    /// editor connected to; with only the gadget (CMS 13's) a note instead, as the link would open an empty overview.
    /// </summary>
    private static void ProjectLink(JsonElement result, int project)
    {
        if (EditorUi == "projectMode")
        {
            Assert.Equal($"{new Uri(McpSiteSettings.Url!, McpSiteSettings.EditUiPath)}#context=epi.cms.project:///{project}", result.GetProperty("projectUrl").GetString(), ignoreCase: true);
            Assert.False(result.TryGetProperty("projectNote", out _));
        }
        else
        {
            Assert.False(result.TryGetProperty("projectUrl", out _));
            Assert.Contains("Projects gadget", result.GetProperty("projectNote").GetString());
        }
    }
}
