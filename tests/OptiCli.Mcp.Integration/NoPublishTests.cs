using System.Net;
using System.Text.Json;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// The second configuration: the same site started with <c>OptiCli__Mcp__AllowPublish=false</c> (serve.sh
/// <c>--no-publish</c>), which is how a site that hasn't opted in to publishing looks. Drafts and review requests still
/// work; publishing in any form is refused with a hint that says so.
/// </summary>
[Collection(McpSiteCollection.Name)]
public sealed class NoPublishTests
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
            await editor.OkAsync("delete_content", new { reference = page });
        }
    }

    private static void Refused(JsonElement error)
    {
        Assert.Equal(("refused", "publishingOff"), (error.GetProperty("code").GetString(), error.GetProperty("reason").GetString()));
        Assert.Contains("draft", error.GetProperty("hint").GetString());
        Assert.Contains("Nothing was changed", error.GetProperty("hint").GetString());
    }
}
