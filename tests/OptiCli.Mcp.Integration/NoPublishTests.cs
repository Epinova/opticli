using System.Net;
using System.Text.Json;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// The second configuration: the same site started with <c>OptiCli__Mcp__AllowPublish=false</c> and
/// <c>OptiCli__Mcp__AllowDelete=false</c> (serve.sh <c>--no-publish</c>), which is how a site that hasn't opted in to
/// either looks. Drafts and review requests still work; publishing in any form is refused with a hint that says so, and
/// so is what else changes what visitors see or deletes someone else's work. Scratch content is cleaned up through the
/// first configuration's site, which allows deleting.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class NoPublishTests(SharedSessions sessions)
{
    [NoPublishSiteFact]
    public async Task A_site_that_does_not_allow_publishing_offers_no_publish_scope_and_refuses_every_publish()
    {
        var site = McpSiteSettings.NoPublishUrl!;
        using (var http = new HttpClient { BaseAddress = site })
        {
            using var metadata = await http.GetAsync(".well-known/oauth-authorization-server/episerver/opticli");
            var scopes = SignInTests.Strings(JsonDocument.Parse(await metadata.Content.ReadAsStringAsync()).RootElement.GetProperty("scopes_supported"));
            Assert.Equal(["content:read", "content:write"], scopes);
        }

        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = site });
        Assert.DoesNotContain("Publish and unpublish content", editor.Browser.LastConsentPage);
        Assert.Contains("doesn't let AI assistants publish", editor.Browser.LastConsentPage);
        var me = await editor.OkAsync("whoami");
        Assert.Equal(["content:read", "content:write"], SignInTests.Strings(me.GetProperty("scopes")));
        Assert.False(me.GetProperty("site").GetProperty("allowPublish").GetBoolean());

        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = $"opticli-mcp-it {Guid.NewGuid():N}";
        Refused(await editor.ErrorAsync("create_content", new { type = "StandardPage", name, parent = start, publish = true }));
        Refused(await editor.ErrorAsync("create_content", new { type = "StandardPage", name, parent = start, publishAt = DateTimeOffset.Now.AddDays(1) }));
        Assert.Empty((await editor.OkAsync("find_content", new { name, root = start })).GetProperty("items").EnumerateArray());

        // A draft is fine, and the editor publishes it in the CMS.
        var created = await editor.OkAsync("create_content", new { type = "StandardPage", name, parent = start });
        var page = WritingTests.Id(created.GetProperty("content"));
        try
        {
            WritingTests.EditUrl(created, created.GetProperty("content").GetProperty("ref").GetString()!, site);
            Refused(await editor.ErrorAsync("update_content", new { reference = page, properties = new { TeaserText = "x" }, publish = true }));
            Refused(await editor.ErrorAsync("publish_content", new { reference = page }));
            Refused(await editor.ErrorAsync("unpublish_content", new { reference = page, dryRun = true }));
            // A review request where no approval sequence applies is no way round it: there is nothing to review.
            foreach (var (tool, arguments) in new (string, object)[]
            {
                ("publish_content", new { reference = page, requestApproval = true }),
                ("update_content", new { reference = page, properties = new { TeaserText = "x" }, requestApproval = true }),
                ("add_language", new { reference = page, lang = "sv", requestApproval = true }),
            })
            {
                var noSequence = await editor.ErrorAsync(tool, arguments);
                Assert.Equal(("usage", "noApprovalSequence"), (noSequence.GetProperty("code").GetString(), noSequence.GetProperty("reason").GetString()));
            }
            var versions = (await editor.OkAsync("list_versions", new { reference = page })).GetProperty("versions");
            Assert.Equal("checkedOut", Assert.Single(versions.EnumerateArray()).GetProperty("status").GetString());
        }
        finally
        {
            await WritingTests.DeleteAsync(await sessions.ForAsync(TestUsers.Editor), page);
        }
    }

    [NoPublishSiteFact]
    public async Task A_move_that_may_change_what_visitors_see_is_refused_and_one_of_a_draft_is_made()
    {
        // Made on the first site, which publishes; moved on this one, which doesn't.
        var main = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await main.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var published = WritingTests.Id((await main.OkAsync("create_content", new { type = "StandardPage", name = Scratch(), parent = start, publish = true })).GetProperty("content"));
        var container = WritingTests.Id((await main.OkAsync("create_content", new { type = "StandardPage", name = Scratch(), parent = start })).GetProperty("content"));
        await main.OkAsync("create_content", new { type = "StandardPage", name = Scratch(), parent = container, publish = true });
        var draft = WritingTests.Id((await main.OkAsync("create_content", new { type = "StandardPage", name = Scratch(), parent = start })).GetProperty("content"));
        try
        {
            await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = McpSiteSettings.NoPublishUrl });
            foreach (var dryRun in new[] { true, false })
            {
                // Published: its URL changes at once, and it takes the access rights of where it goes.
                Refused(await editor.ErrorAsync("move_content", new { reference = published, destination = container, dryRun }));
                // Never published itself, but with published content below it.
                Refused(await editor.ErrorAsync("move_content", new { reference = container, destination = published, dryRun }));
            }
            // A draft that was never published, with nothing below it: nothing visitors see changes.
            var moved = await editor.OkAsync("move_content", new { reference = draft, destination = container });
            Assert.Equal(container, moved.GetProperty("parent").GetString());
        }
        finally
        {
            await WritingTests.DeleteAsync(main, published);
            await WritingTests.DeleteAsync(main, container);
        }
    }

    [NoPublishSiteFact]
    public async Task Discarding_what_someone_else_saved_needs_deleting_and_a_scheduled_version_publishing()
    {
        // The two instances share the database but not their caches (no remote events between them), so each version is
        // listed only on an instance that hadn't read the content's versions before it was saved.
        var site = McpSiteSettings.NoPublishUrl;
        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = site });
        await using var admin = await McpSession.ConnectAsync(TestUsers.Admin, new SessionOptions { Site = site });
        var main = await sessions.ForAsync(TestUsers.Editor);
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));

        // The editor's own draft: discarding it deletes nothing of anyone else's, so it needs no AllowDelete.
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name = Scratch(), parent = start })).GetProperty("content"));
        var scheduled = WritingTests.Id((await main.OkAsync("create_content", new { type = "StandardPage", name = Scratch(), parent = start, publish = true })).GetProperty("content"));
        try
        {
            await editor.OkAsync("update_content", new { reference = page, name = "second draft" });
            Assert.True((await editor.OkAsync("discard_draft", new { reference = page })).GetProperty("discarded").GetBoolean());

            // Someone else's draft: includeDraft only confirms it; it takes what deleting takes.
            var theirs = (await admin.OkAsync("update_content", new { reference = page, name = "the administrator's draft" })).GetProperty("content").GetProperty("ref").GetString()!;
            foreach (var dryRun in new[] { true, false })
            {
                var refused = await editor.ErrorAsync("discard_draft", new { reference = theirs, includeDraft = true, dryRun });
                Assert.True(refused.TryGetProperty("reason", out var reason) && reason.GetString() == "deletingOff", refused.GetRawText());
            }
            // Where the site allows deleting, as before.
            Assert.True((await main.OkAsync("discard_draft", new { reference = theirs, includeDraft = true })).GetProperty("discarded").GetBoolean());

            // A scheduled version: discarding it cancels a publish.
            var version = (await main.OkAsync("update_content", new { reference = scheduled, name = "scheduled", publishAt = DateTimeOffset.Now.AddDays(7) })).GetProperty("content").GetProperty("ref").GetString()!;
            foreach (var dryRun in new[] { true, false })
            {
                Refused(await editor.ErrorAsync("discard_draft", new { reference = version, dryRun }));
            }
            Assert.Contains((await main.OkAsync("list_versions", new { reference = scheduled })).GetProperty("versions").EnumerateArray(),
                v => v.GetProperty("status").GetString() == "delayedPublish");
        }
        finally
        {
            await WritingTests.DeleteAsync(main, page);
            await WritingTests.DeleteAsync(main, scheduled);
        }
    }

    private static string Scratch() => $"opticli-mcp-it {Guid.NewGuid():N}";

    private static void Refused(JsonElement error)
    {
        Assert.Equal(("refused", "publishingOff"), (error.GetProperty("code").GetString(), error.GetProperty("reason").GetString()));
        Assert.Contains("draft", error.GetProperty("hint").GetString());
        Assert.Contains("Nothing was changed", error.GetProperty("hint").GetString());
    }
}
