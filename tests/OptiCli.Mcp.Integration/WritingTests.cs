using System.Globalization;
using System.Text.Json;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// Writing as the editor: drafts in their name, the CMS's access rights, approval sequences and concurrency, and an
/// edit UI link on every result. Each test deletes the content it creates (to the recycle bin, as the edit UI does).
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class WritingTests(SharedSessions sessions)
{
    private static string ScratchName() => $"opticli-mcp-it {Guid.NewGuid():N}";

    [McpSiteFact]
    public async Task Create_update_publish_discard_and_delete_round_trip_as_the_editor_with_edit_links()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();

        var dryRun = await editor.OkAsync("create_content", new { type = "StandardPage", name, parent = start, dryRun = true, properties = new { TeaserText = "first" } });
        Assert.True(dryRun.GetProperty("dryRun").GetBoolean());
        Assert.False(dryRun.GetProperty("saved").GetBoolean());
        Assert.Empty((await editor.OkAsync("find_content", new { name, root = start })).GetProperty("items").EnumerateArray());

        var created = await editor.OkAsync("create_content", new { type = "StandardPage", name, parent = start, properties = new { TeaserText = "first" } });
        var page = Id(created.GetProperty("content"));
        try
        {
            Assert.True(created.GetProperty("saved").GetBoolean());
            Assert.False(created.GetProperty("published").GetBoolean());
            var firstVersion = created.GetProperty("content").GetProperty("version").GetInt32();
            var editUrl = EditUrl(created, $"{page}_{firstVersion}");

            // The draft is the editor's: the CMS recorded them, not a service account.
            var versions = await editor.OkAsync("list_versions", new { reference = page });
            var draft = Assert.Single(versions.GetProperty("versions").EnumerateArray());
            Assert.Equal((TestUsers.Editor, "checkedOut"), (draft.GetProperty("savedBy").GetString(), draft.GetProperty("status").GetString()));
            Assert.Equal(TestUsers.Editor, (await editor.OkAsync("get_content", new { reference = page, version = "latest" })).GetProperty("changedBy").GetString());
            // The edit link resolves to the draft it names.
            var linked = await editor.OkAsync("resolve_url", new { url = editUrl });
            Assert.Equal($"{page}_{firstVersion}", linked.GetProperty("content").GetProperty("ref").GetString());

            var published = await editor.OkAsync("publish_content", new { reference = page });
            Assert.True(published.GetProperty("published").GetBoolean());
            EditUrl(published, $"{page}_{firstVersion}");

            var preview = await editor.OkAsync("update_content", new { reference = page, properties = new { TeaserText = "second" }, baseVersion = firstVersion, dryRun = true });
            Assert.False(preview.GetProperty("saved").GetBoolean());
            Assert.Contains(preview.GetProperty("changes").EnumerateArray(), c => c.GetProperty("property").GetString() == "TeaserText");

            var updated = await editor.OkAsync("update_content", new { reference = page, properties = new { TeaserText = "second" }, baseVersion = firstVersion });
            Assert.True(updated.GetProperty("saved").GetBoolean());
            var secondVersion = updated.GetProperty("content").GetProperty("version").GetInt32();
            Assert.NotEqual(firstVersion, secondVersion);
            EditUrl(updated, $"{page}_{secondVersion}");
            Assert.Equal("second", Property(await editor.OkAsync("get_content", new { reference = page, version = "latest" }), "TeaserText"));
            Assert.Equal("first", Property(await editor.OkAsync("get_content", new { reference = page }), "TeaserText"));

            var discarded = await editor.OkAsync("discard_draft", new { reference = page });
            Assert.True(discarded.GetProperty("discarded").GetBoolean());
            Assert.Equal($"{page}_{secondVersion}", discarded.GetProperty("content").GetProperty("ref").GetString());
            // The link is to the content now: the version it named is gone.
            EditUrl(discarded, page);
            Assert.DoesNotContain((await editor.OkAsync("list_versions", new { reference = page })).GetProperty("versions").EnumerateArray(),
                v => v.GetProperty("version").GetInt32() == secondVersion);
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
        var deleted = await editor.OkAsync("get_content", new { reference = page });
        Assert.True(deleted.GetProperty("deleted").GetBoolean());
    }

    [McpSiteFact]
    public async Task A_save_from_an_older_version_than_the_latest_is_refused_as_a_conflict()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var created = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, publish = true });
        var page = Id(created.GetProperty("content"));
        try
        {
            var read = await editor.OkAsync("get_content", new { reference = page });
            var baseVersion = read.GetProperty("version").GetInt32();
            // Someone else (here: another tab of the same editor) saves first.
            await editor.OkAsync("update_content", new { reference = page, properties = new { TeaserText = "theirs" }, baseVersion });

            var conflict = await editor.ErrorAsync("update_content", new { reference = page, properties = new { TeaserText = "mine" }, baseVersion });
            Assert.Equal("conflict", conflict.GetProperty("code").GetString());
            Assert.Equal("theirs", Property(await editor.OkAsync("get_content", new { reference = page, version = "latest" }), "TeaserText"));
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task An_editor_who_may_edit_but_not_publish_saves_drafts_and_is_refused_a_publish()
    {
        var product = await sessions.ForAsync(TestUsers.Product);
        var hidden = await product.OkAsync("get_content", new { reference = TestUsers.HiddenPage });
        var page = Id(hidden);
        var publishedVersion = hidden.GetProperty("version").GetInt32();
        var before = (await product.OkAsync("list_versions", new { reference = page })).GetProperty("versions").GetArrayLength();

        var refused = await product.ErrorAsync("update_content", new { reference = page, name = "Alloy Meet (published by an assistant)", publish = true });
        Assert.Equal(("refused", "accessDenied"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
        Assert.Contains("Publish", refused.GetProperty("message").GetString());
        Assert.Equal(before, (await product.OkAsync("list_versions", new { reference = page })).GetProperty("versions").GetArrayLength());

        var draft = await product.OkAsync("update_content", new { reference = page, name = $"Alloy Meet (draft {Guid.NewGuid():N})" });
        var version = draft.GetProperty("content").GetProperty("version").GetInt32();
        try
        {
            Assert.True(draft.GetProperty("saved").GetBoolean());
            Assert.False(draft.GetProperty("published").GetBoolean());
            Assert.Equal(TestUsers.Product, (await product.OkAsync("get_content", new { reference = page, version = "latest" })).GetProperty("changedBy").GetString());
            // Publishing the draft is refused too, by the CMS's own check; and so is deleting it, which needs Delete.
            var publish = await product.ErrorAsync("publish_content", new { reference = page });
            Assert.Equal("accessDenied", publish.GetProperty("reason").GetString());
            var discard = await product.ErrorAsync("discard_draft", new { reference = $"{page}_{version}" });
            Assert.Equal("refused", discard.GetProperty("code").GetString());
            Assert.Contains("Delete access", discard.GetProperty("message").GetString());
        }
        finally
        {
            // An administrator throws the draft away: someone else's, which takes includeDraft.
            var admin = await sessions.ForAsync(TestUsers.Admin);
            var theirs = await admin.ErrorAsync("discard_draft", new { reference = $"{page}_{version}" });
            Assert.Equal(TestUsers.Product, theirs.GetProperty("pendingDraft").GetProperty("savedBy").GetString());
            await admin.OkAsync("discard_draft", new { reference = $"{page}_{version}", includeDraft = true });
        }
        var after = await product.OkAsync("get_content", new { reference = page });
        Assert.Equal((publishedVersion, "Alloy Meet"), (after.GetProperty("version").GetInt32(), after.GetProperty("name").GetString()));
    }

    [McpSiteFact]
    public async Task A_publish_under_an_approval_sequence_is_refused_and_a_review_request_starts_it()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var root = Id(await editor.OkAsync("get_content", new { reference = TestUsers.ApprovalRoot }));
        var created = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = root });
        var page = Id(created.GetProperty("content"));
        try
        {
            // Even an editor with Publish rights doesn't publish past the reviewers: the CMS itself would let them.
            var refused = await editor.ErrorAsync("update_content", new { reference = page, name = "published past review", publish = true });
            Assert.Equal(("refused", "approvalSequence"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
            Assert.Contains("requestApproval", refused.GetProperty("hint").GetString());
            var direct = await editor.ErrorAsync("publish_content", new { reference = page });
            Assert.Equal("approvalSequence", direct.GetProperty("reason").GetString());

            var review = await editor.OkAsync("update_content", new { reference = page, name = "for review", requestApproval = true });
            Assert.True(review.GetProperty("approvalRequested").GetBoolean());
            Assert.False(review.GetProperty("published").GetBoolean());
            Assert.Equal("awaitingApproval", review.GetProperty("content").GetProperty("status").GetString());
            EditUrl(review, review.GetProperty("content").GetProperty("ref").GetString()!);

            // In review, it can't be changed until a reviewer decides.
            var inReview = await editor.ErrorAsync("update_content", new { reference = page, name = "changed meanwhile" });
            Assert.Equal(("conflict", "inReview"), (inReview.GetProperty("code").GetString(), inReview.GetProperty("reason").GetString()));
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    [McpSiteFact]
    public async Task A_review_request_never_publishes_and_needs_no_publish_scope()
    {
        // The editor has Publish rights, but left publishing unticked on the consent page.
        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Allow = ["content:write"] });
        Assert.Equal(["content:read", "content:write"], SignInTests.Strings((await editor.OkAsync("whoami")).GetProperty("scopes")));
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var root = Id(await editor.OkAsync("get_content", new { reference = TestUsers.ApprovalRoot }));
        var plain = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        var reviewed = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = root })).GetProperty("content"));
        try
        {
            // No sequence applies: there is nothing to review, and requestApproval is no way round the publish gate.
            NoSequence(await editor.ErrorAsync("publish_content", new { reference = plain, requestApproval = true }));
            NoSequence(await editor.ErrorAsync("update_content", new { reference = plain, name = "x", requestApproval = true }));
            Assert.Equal("checkedOut", Assert.Single((await editor.OkAsync("list_versions", new { reference = plain })).GetProperty("versions").EnumerateArray()).GetProperty("status").GetString());
            var unscoped = await editor.ErrorAsync("publish_content", new { reference = plain });
            Assert.Equal(("refused", "missingScope"), (unscoped.GetProperty("code").GetString(), unscoped.GetProperty("reason").GetString()));

            // Where a sequence applies, sending it for review is what an editor without publishing does.
            var review = await editor.OkAsync("publish_content", new { reference = reviewed, requestApproval = true });
            Assert.True(review.GetProperty("approvalRequested").GetBoolean());
            Assert.False(review.GetProperty("published").GetBoolean());
            Assert.Equal("awaitingApproval", review.GetProperty("content").GetProperty("status").GetString());
        }
        finally
        {
            await DeleteAsync(editor, plain);
            await DeleteAsync(editor, reviewed);
        }
    }

    [McpSiteFact]
    public async Task Content_in_the_recycle_bin_is_not_restored_through_a_move()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start, publish = true })).GetProperty("content"));
        await DeleteAsync(editor, page);

        foreach (var dryRun in new[] { true, false })
        {
            var refused = await editor.ErrorAsync("move_content", new { reference = page, destination = start, dryRun });
            Assert.Equal("refused", refused.GetProperty("code").GetString());
            Assert.Contains("recycle bin", refused.GetProperty("message").GetString());
            Assert.Contains("CMS edit UI", refused.GetProperty("hint").GetString());
        }
        Assert.True((await editor.OkAsync("get_content", new { reference = page })).GetProperty("deleted").GetBoolean());
    }

    [McpSiteFact]
    public async Task A_content_type_the_editor_may_not_create_is_refused_and_an_administrator_may()
    {
        // RestrictedBlock (McpFixture.cs): only WebAdmins may create it, as its access rights in admin mode say.
        var start = Id((await (await sessions.ForAsync(TestUsers.Editor)).OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var admin = await sessions.ForAsync(TestUsers.Admin);
        var arguments = new { type = "RestrictedBlock", name = ScratchName(), forContent = start, dryRun = true };

        var refused = await editor.OkAsync("create_content", arguments);
        Assert.False(refused.GetProperty("valid").GetBoolean());
        Assert.Contains(refused.GetProperty("validation").EnumerateArray(), i => i.GetProperty("message").GetString()!.Contains("may not create RestrictedBlock"));
        var saved = await editor.ErrorAsync("create_content", arguments with { dryRun = false });
        Assert.Equal("validation", saved.GetProperty("code").GetString());

        var allowed = await admin.OkAsync("create_content", arguments);
        Assert.True(allowed.GetProperty("valid").GetBoolean());
        // A type anyone may create stays allowed for the editor.
        Assert.True((await editor.OkAsync("create_content", arguments with { type = "EditorialBlock" })).GetProperty("valid").GetBoolean());
    }

    [McpSiteFact]
    public async Task A_language_branch_is_not_published_before_its_master_and_the_hint_says_what_to_do()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        // The master branch (en) is only a draft.
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            var refused = await editor.ErrorAsync("add_language", new { reference = page, lang = "sv", publish = true });
            Assert.Equal(("validation", "masterNotPublished"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
            var hint = refused.GetProperty("hint").GetString()!;
            Assert.Contains("publish_content", hint);
            Assert.Contains("draft", hint);
            Assert.DoesNotContain("opticli", hint);
            Assert.Single((await editor.OkAsync("list_versions", new { reference = page })).GetProperty("versions").EnumerateArray());

            // As a draft it is fine.
            var draft = await editor.OkAsync("add_language", new { reference = page, lang = "sv" });
            Assert.False(draft.GetProperty("published").GetBoolean());
        }
        finally
        {
            await DeleteAsync(editor, page);
        }
    }

    private static void NoSequence(JsonElement error)
    {
        Assert.Equal(("usage", "noApprovalSequence"), (error.GetProperty("code").GetString(), error.GetProperty("reason").GetString()));
        Assert.Contains("Nothing was saved", error.GetProperty("hint").GetString());
    }

    [McpSiteFact]
    public async Task An_upload_goes_into_the_page_s_own_folder_as_a_draft_of_the_editor()
    {
        // A 1x1 PNG: Alloy's ImageFile takes .png.
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var start = Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var page = Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start })).GetProperty("content"));
        try
        {
            var preview = await editor.OkAsync("upload_media", new { fileName = "pixel.png", forContent = page, dryRun = true });
            Assert.Equal("ImageFile", preview.GetProperty("mediaType").GetString());

            var uploaded = await editor.OkAsync("upload_media", new { fileName = "pixel.png", data = png, forContent = page });
            Assert.True(uploaded.GetProperty("saved").GetBoolean());
            var media = uploaded.GetProperty("content");
            EditUrl(uploaded, media.GetProperty("ref").GetString()!);
            var read = await editor.OkAsync("get_content", new { reference = Id(media), version = "latest" });
            Assert.Equal((TestUsers.Editor, "ImageFile"), (read.GetProperty("changedBy").GetString(), read.GetProperty("type").GetString()));
        }
        finally
        {
            // The page's own folder stays where it is, as in the CMS: it goes when the recycle bin is emptied.
            await DeleteAsync(editor, page);
        }
    }

    /// <summary>Moves scratch content to the recycle bin.</summary>
    internal static async Task DeleteAsync(McpSession session, string reference)
    {
        var deleted = await session.OkAsync("delete_content", new { reference });
        EditUrl(deleted, reference);
    }

    internal static string Id(JsonElement content) => content.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);

    internal static string? Property(JsonElement content, string name) =>
        content.GetProperty("properties").GetProperty(name).TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The result's link into the CMS edit UI, on the site the editor connected to, at the version (or content) it names.</summary>
    internal static string EditUrl(JsonElement result, string reference, Uri? site = null)
    {
        var url = result.GetProperty("editUrl").GetString()!;
        Assert.StartsWith(new Uri(site ?? McpSiteSettings.Url!, "EPiServer/CMS/").ToString(), url, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith($"#context=epi.cms.contentdata:///{reference}", url);
        return url;
    }
}
