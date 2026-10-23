using System.Globalization;
using System.Net;
using System.Text.Json;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// The assistant's own preview links (<c>AllowPreviewLinks</c>): on the third configuration (serve.sh
/// <c>--preview-links</c>), a draft's link opens for a client with no cookie and no header, shows that one version and
/// nothing else, and stops working when the connection does or the editor can no longer read the version; on the
/// default one, off by default, there is no such link and no such path. Never the CMS's preview token, in either.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class PreviewLinkTests(SharedSessions sessions)
{
    private const string NotValid = "This preview link has expired or isn't valid";

    private static string ScratchName() => $"opticli-mcp-it {Guid.NewGuid():N}";

    /// <summary>What an assistant's web fetch is: no cookies, no redirects followed.</summary>
    private static HttpClient Anonymous() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    [PreviewLinksSiteFact]
    public async Task A_drafts_link_opens_without_a_sign_in_and_shows_that_version_and_nothing_else()
    {
        var site = McpSiteSettings.PreviewLinksUrl!;
        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = site });
        var me = (await editor.OkAsync("whoami")).GetProperty("site");
        Assert.True(me.GetProperty("allowPreviewLinks").GetBoolean());
        Assert.Equal(600, me.GetProperty("previewLinkLifetimeSeconds").GetInt32());
        Assert.Contains("assistantPreviewUrl", editor.Client.ServerInstructions);

        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        var created = await editor.OkAsync("create_content", new { type = "StandardPage", name, parent = start, publish = true });
        var page = WritingTests.Id(created.GetProperty("content"));
        var deleted = false;
        try
        {
            // Published: nothing to preview, for the user or the assistant.
            Assert.False(created.TryGetProperty("assistantPreviewUrl", out _));
            Assert.False((await editor.OkAsync("get_content", new { reference = page })).TryGetProperty("assistantPreviewUrl", out _));
            var baseVersion = created.GetProperty("content").GetProperty("version").GetInt32();
            var draftName = $"{name} draft";
            Assert.False((await editor.OkAsync("update_content", new { reference = page, name = draftName, baseVersion, dryRun = true })).TryGetProperty("assistantPreviewUrl", out _));

            var updated = await editor.OkAsync("update_content", new { reference = page, name = draftName, baseVersion });
            var link = updated.GetProperty("assistantPreviewUrl").GetString()!;
            Assert.StartsWith(new Uri(site, "episerver/opticli/preview/").ToString(), link, StringComparison.Ordinal);
            var expires = DateTimeOffset.Parse(updated.GetProperty("assistantPreviewExpires").GetString()!, CultureInfo.InvariantCulture);
            Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));
            // The user's own link is unchanged, and neither carries the CMS's preview token.
            Assert.Contains(",,", updated.GetProperty("previewUrl").GetString());
            Assert.DoesNotContain("preview_token", updated.GetRawText());

            using var anonymous = Anonymous();
            using (var response = await anonymous.GetAsync(link))
            {
                var html = await response.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains(draftName, html);
                Assert.DoesNotContain("preview_token", html);
                Assert.False(response.Headers.Contains("Set-Cookie"));
                Assert.True(response.Headers.CacheControl!.NoStore);
                Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
                Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            }
            using (var head = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, link)))
            {
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                Assert.False(head.Headers.Contains("Set-Cookie"));
            }

            // Only that link, as it was given: another path, a query of any kind, another spelling, another host, another method.
            var refused = new List<HttpRequestMessage>
            {
                new(HttpMethod.Get, link + "x"),
                new(HttpMethod.Get, link[..^4] + (link[^4] == 'A' ? 'B' : 'A') + link[^3..]),
                new(HttpMethod.Get, link + "/extra"),
                new(HttpMethod.Get, link + "?preview_token=abc"),
                new(HttpMethod.Get, link + "?epieditmode=true"),
                new(HttpMethod.Get, new Uri(site, "episerver/opticli/preview/")),
                // Other spellings of the same payload, and the same link on another host.
                new(HttpMethod.Get, link + "=="),
                new(HttpMethod.Get, link.Replace("/preview/", "/PREVIEW/", StringComparison.Ordinal)),
                new(HttpMethod.Get, link) { Headers = { Host = "evil.example.com" } },
                new(HttpMethod.Get, link) { Headers = { Host = "localhost:" + site.Port } },
            };
            foreach (var request in refused)
            {
                using var response = await anonymous.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Contains(NotValid, await response.Content.ReadAsStringAsync());
                Assert.Null(response.Headers.Location);
            }
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            {
                using var response = await anonymous.SendAsync(new HttpRequestMessage(method, link));
                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                Assert.Equal(["GET", "HEAD"], response.Content.Headers.Allow.Order());
            }

            // Read again, the draft gets a fresh link; the published version gets none.
            var again = (await editor.OkAsync("get_content", new { reference = page, version = "latest" })).GetProperty("assistantPreviewUrl").GetString()!;
            Assert.NotEqual(link, again);
            Assert.False((await editor.OkAsync("get_content", new { reference = page })).TryGetProperty("assistantPreviewUrl", out _));

            // Published meanwhile, the version is what visitors see: the link still shows it.
            await editor.OkAsync("publish_content", new { reference = page });
            using (var response = await anonymous.GetAsync(link))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains(draftName, await response.Content.ReadAsStringAsync());
            }

            // In the recycle bin, it shows nothing.
            deleted = true;
            await editor.OkAsync("delete_content", new { reference = page });
            using (var response = await anonymous.GetAsync(again))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        finally
        {
            if (!deleted)
            {
                await editor.OkAsync("delete_content", new { reference = page });
            }
        }
    }

    [PreviewLinksSiteFact]
    public async Task A_block_draft_and_a_translation_have_links_of_their_own()
    {
        var site = McpSiteSettings.PreviewLinksUrl!;
        await using var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = site });
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var name = ScratchName();
        var page = WritingTests.Id((await editor.OkAsync("create_content", new { type = "StandardPage", name, parent = start })).GetProperty("content"));
        try
        {
            using var anonymous = Anonymous();

            // A block, through the site's preview template.
            var marker = $"opticli block {Guid.NewGuid():N}";
            var block = await editor.OkAsync("create_content", new { type = "EditorialBlock", name = ScratchName(), forContent = page, properties = new { MainBody = $"<p>{marker}</p>" } });
            using (var response = await anonymous.GetAsync(block.GetProperty("assistantPreviewUrl").GetString()!))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains(marker, await response.Content.ReadAsStringAsync());
            }

            // A language branch: its own version, in its own language.
            var swedish = $"{name} sv";
            var translated = await editor.OkAsync("add_language", new { reference = page, lang = "sv", name = swedish });
            using (var response = await anonymous.GetAsync(translated.GetProperty("assistantPreviewUrl").GetString()!))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains(swedish, await response.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            await editor.OkAsync("delete_content", new { reference = page });
        }
    }

    [PreviewLinksSiteFact]
    public async Task A_link_stops_working_when_its_connection_is_revoked()
    {
        var site = McpSiteSettings.PreviewLinksUrl!;
        var editor = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Site = site });
        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var created = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start });
        var page = WritingTests.Id(created.GetProperty("content"));
        try
        {
            var link = created.GetProperty("assistantPreviewUrl").GetString()!;
            using var anonymous = Anonymous();
            using (var response = await anonymous.GetAsync(link))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // Revoked on the connections page (the session's dispose presses Revoke).
            await editor.DisposeAsync();
            using (var response = await anonymous.GetAsync(link))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Contains(NotValid, await response.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            await WritingTests.DeleteAsync(await sessions.ForAsync(TestUsers.Editor), page);
        }
    }

    [PreviewLinksSiteFact]
    public async Task A_link_stops_working_when_the_editor_can_no_longer_read_the_version()
    {
        // The administrator may read the hidden page through WebAdmins; with only WebEditors left, they may not.
        var site = McpSiteSettings.PreviewLinksUrl!;
        await using var admin = await McpSession.ConnectAsync(TestUsers.Admin, new SessionOptions { Site = site });
        var hidden = WritingTests.Id(await admin.OkAsync("get_content", new { reference = TestUsers.HiddenPage }));
        var draft = await admin.OkAsync("update_content", new { reference = hidden, name = $"Alloy Meet (draft {Guid.NewGuid():N})" });
        var version = draft.GetProperty("content").GetProperty("version").GetInt32();
        try
        {
            var link = draft.GetProperty("assistantPreviewUrl").GetString()!;
            using var anonymous = Anonymous();
            using (var response = await anonymous.GetAsync(link))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var refresh = admin.Tokens.Current!.RefreshToken!;
            await using (await TestUserRoles.ForTestSite().RemoveAsync(TestUsers.Admin, "WebAdmins"))
            {
                // The connection's roles are the ones of its last refresh, as for every tool call.
                var (status, body) = await admin.RefreshAsync(refresh);
                Assert.Equal(HttpStatusCode.OK, status);
                refresh = body.GetProperty("refresh_token").GetString()!;
                using var response = await anonymous.GetAsync(link);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Contains(NotValid, await response.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.OK, (await admin.RefreshAsync(refresh)).Status);
        }
        finally
        {
            // Through the default site's connection, whose roles are those of its own sign-in, should this one have lost them.
            await (await sessions.ForAsync(TestUsers.Admin)).OkAsync("discard_draft", new { reference = $"{hidden}_{version}" });
        }
    }

    [PreviewLinksSiteFact]
    public async Task A_preview_the_cms_refuses_the_editor_is_a_plain_not_found_never_a_redirect()
    {
        // A product editor may read and edit the hidden page, but isn't one of the CMS's preview roles (CmsEditors):
        // the CMS sends them to its login, which a preview link never passes on.
        var site = McpSiteSettings.PreviewLinksUrl!;
        await using var product = await McpSession.ConnectAsync(TestUsers.Product, new SessionOptions { Site = site });
        var hidden = WritingTests.Id(await product.OkAsync("get_content", new { reference = TestUsers.HiddenPage }));
        var draft = await product.OkAsync("update_content", new { reference = hidden, name = $"Alloy Meet (draft {Guid.NewGuid():N})" });
        var version = draft.GetProperty("content").GetProperty("version").GetInt32();
        try
        {
            using var anonymous = Anonymous();
            using var response = await anonymous.GetAsync(draft.GetProperty("assistantPreviewUrl").GetString()!);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Contains(NotValid, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            var admin = await sessions.ForAsync(TestUsers.Admin);
            await admin.OkAsync("discard_draft", new { reference = $"{hidden}_{version}", includeDraft = true });
        }
    }

    [McpSiteFact]
    public async Task A_site_that_does_not_allow_preview_links_gives_none_and_has_no_such_path()
    {
        var editor = await sessions.ForAsync(TestUsers.Editor);
        var me = (await editor.OkAsync("whoami")).GetProperty("site");
        Assert.False(me.GetProperty("allowPreviewLinks").GetBoolean());
        Assert.False(me.TryGetProperty("previewLinkLifetimeSeconds", out _));
        Assert.DoesNotContain("fetch assistantPreviewUrl", editor.Client.ServerInstructions);

        var start = WritingTests.Id((await editor.OkAsync("resolve_url", new { url = "/en/" })).GetProperty("content"));
        var created = await editor.OkAsync("create_content", new { type = "StandardPage", name = ScratchName(), parent = start });
        var page = WritingTests.Id(created.GetProperty("content"));
        try
        {
            Assert.True(created.TryGetProperty("previewUrl", out var previewUrl));
            Assert.DoesNotContain("preview_token", previewUrl.GetString());
            Assert.False(created.TryGetProperty("assistantPreviewUrl", out _));
            Assert.False((await editor.OkAsync("get_content", new { reference = page, version = "latest" })).TryGetProperty("assistantPreviewUrl", out _));

            // The path is the site's like any other: its own 404 page, not the module's answer.
            using var anonymous = Anonymous();
            using var response = await anonymous.GetAsync(new Uri(McpSiteSettings.Url!, "episerver/opticli/preview/x"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(NotValid, await response.Content.ReadAsStringAsync());
            Assert.False(response.Headers.Contains("X-Robots-Tag"));
        }
        finally
        {
            await WritingTests.DeleteAsync(editor, page);
        }
    }
}
