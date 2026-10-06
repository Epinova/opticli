using System.Net;
using System.Text.Json;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>Connecting as Claude does: discovery from the 401, registration, the site's own login, consent, the role gate.</summary>
[Collection(McpSiteCollection.Name)]
public sealed class SignInTests(SharedSessions sessions)
{
    [McpSiteFact]
    public async Task A_client_finds_the_authorization_server_registers_signs_in_and_calls_tools_as_the_editor()
    {
        var site = McpSiteSettings.Url!;
        using var http = new HttpClient { BaseAddress = site };

        // Discovery: a plain 401 that points to the metadata, never a redirect to the login page.
        using var anonymous = await http.PostAsync(McpSession.McpPath, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var challenge = anonymous.Headers.WwwAuthenticate.ToString();
        var metadataUrl = new Uri(site, ".well-known/oauth-protected-resource/episerver/opticli/mcp");
        Assert.Contains($"resource_metadata=\"{metadataUrl}\"", challenge);
        Assert.Contains("content:publish", challenge);
        var resource = await JsonAsync(http, metadataUrl);
        Assert.Equal(new Uri(site, McpSession.McpPath).ToString(), resource.GetProperty("resource").GetString());
        var issuer = resource.GetProperty("authorization_servers")[0].GetString()!;
        Assert.Equal(new Uri(site, "episerver/opticli").ToString(), issuer);
        var server = await JsonAsync(http, new Uri(site, ".well-known/oauth-authorization-server/episerver/opticli"));
        Assert.Equal(issuer, server.GetProperty("issuer").GetString());
        Assert.True(server.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.Equal(["S256"], server.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
        // The site's own root documents are left alone.
        using var root = await http.GetAsync(".well-known/oauth-authorization-server");
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);

        await using var session = await McpSession.ConnectAsync(TestUsers.Editor);
        Assert.Equal(1, session.Browser.SignIns);
        Assert.Contains(session.Traffic.Requests, r => r.Path.EndsWith("/oauth/register", StringComparison.Ordinal) && r.Status == HttpStatusCode.Created);
        Assert.Equal([HttpStatusCode.OK], session.Traffic.TokenRequests("authorization_code"));
        Assert.Contains("mcp-editor", session.Browser.LastConsentPage);
        Assert.Contains("Publish and unpublish content", session.Browser.LastConsentPage);

        var tools = (await session.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "whoami", "get_content", "list_children", "resolve_url", "find_content", "get_content_type", "list_versions",
            "create_content", "update_content", "add_language", "upload_media", "discard_draft", "move_content",
            "delete_content", "publish_content", "unpublish_content",
        }, tools);

        var me = await session.OkAsync("whoami");
        Assert.Equal(TestUsers.Editor, me.GetProperty("name").GetString());
        Assert.Contains("WebEditors", Strings(me.GetProperty("roles")));
        Assert.Equal(["content:read", "content:write", "content:publish"], Strings(me.GetProperty("scopes")));
        Assert.True(me.GetProperty("site").GetProperty("allowPublish").GetBoolean());
        Assert.True(me.GetProperty("site").GetProperty("allowDelete").GetBoolean());
        Assert.Equal("opticli E2E tests (mcp-editor)", me.GetProperty("client").GetString());
    }

    [McpSiteFact]
    public async Task Only_claudes_and_loopback_return_addresses_may_register_and_the_metadata_is_never_cached()
    {
        using var http = new HttpClient { BaseAddress = McpSiteSettings.Url! };
        async Task<HttpResponseMessage> Register(string redirect) => await http.PostAsync("episerver/opticli/oauth/register", new StringContent(
            JsonSerializer.Serialize(new { redirect_uris = new[] { redirect }, client_name = "Claude", token_endpoint_auth_method = "none" }), System.Text.Encoding.UTF8, "application/json"));

        using var elsewhere = await Register("https://attacker.example/cb");
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Contains("invalid_redirect_uri", await elsewhere.Content.ReadAsStringAsync());
        using var claude = await Register("https://claude.ai/api/mcp/auth_callback");
        Assert.Equal(HttpStatusCode.Created, claude.StatusCode);

        foreach (var path in new[] { ".well-known/oauth-protected-resource/episerver/opticli/mcp", ".well-known/oauth-authorization-server/episerver/opticli" })
        {
            using var metadata = await http.GetAsync(path);
            Assert.True(metadata.Headers.CacheControl?.NoStore, path);
        }
    }

    [McpSiteFact]
    public async Task The_sites_error_pages_and_controllers_leave_the_module_alone()
    {
        using var http = new HttpClient { BaseAddress = McpSiteSettings.Url! };

        // The test site has custom error pages: an error without a body is re-executed as its GET-only error page, so a
        // POST ends in a 405.
        using var page = await http.GetAsync("no-such-page-for-opticli");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Contains("Site error page 404", await page.Content.ReadAsStringAsync());
        using var post = await http.PostAsync("no-such-page-for-opticli", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);

        // The module's 401 is still the module's: the challenge that leads to the metadata, and a JSON body.
        using var anonymous = await http.PostAsync(McpSession.McpPath, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("resource_metadata=", anonymous.Headers.WwwAuthenticate.ToString());
        using var error = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        Assert.Equal("unauthorized", error.RootElement.GetProperty("error").GetString());

        // The site's own named route works: mapped after MapContent() and the site's controllers, the module didn't
        // make the CMS map them twice ("Duplicate endpoint name" on every request).
        using var ping = await http.GetAsync("api/mcp-fixture/ping");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
    }

    [McpSiteFact]
    public async Task A_user_without_an_editor_role_is_turned_away_at_consent()
    {
        var refused = await Assert.ThrowsAsync<PageException>(async () =>
        {
            await using var session = await McpSession.ConnectAsync(TestUsers.Visitor);
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("No access", refused.Title);
        Assert.Contains("mcp-visitor isn't an editor", refused.Message);
    }

    [McpSiteFact]
    public async Task A_role_the_site_adds_to_AllowedRoles_may_connect()
    {
        var session = await sessions.ForAsync(TestUsers.Product);
        var me = await session.OkAsync("whoami");
        Assert.Contains("ProductEditors", Strings(me.GetProperty("roles")));
        Assert.DoesNotContain("WebEditors", Strings(me.GetProperty("roles")));
    }

    [McpSiteFact]
    public async Task A_client_identified_by_a_metadata_document_connects_without_registering()
    {
        var document = new Uri(McpSiteSettings.Url!, "mcp-test-client.json");
        await using var session = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { ClientMetadataDocument = document });

        Assert.DoesNotContain(session.Traffic.Requests, r => r.Path.EndsWith("/oauth/register", StringComparison.Ordinal));
        Assert.Equal(document.ToString(), session.Tokens.Current!.ClientId);
        // The consent page names who publishes the document, and the client by the document's name.
        Assert.Contains("opticli E2E tests (CIMD)", session.Browser.LastConsentPage);
        Assert.Contains($"published by <b>{document.Host}</b>", session.Browser.LastConsentPage);
        var me = await session.OkAsync("whoami");
        Assert.Equal("opticli E2E tests (CIMD)", me.GetProperty("client").GetString());
    }

    [McpSiteFact]
    public async Task Denying_on_the_consent_page_gives_the_client_no_token()
    {
        var denied = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var session = await McpSession.ConnectAsync(TestUsers.Editor, new SessionOptions { Decision = "deny" });
        });
        Assert.Contains("access_denied", Flatten(denied));
    }

    private static async Task<JsonElement> JsonAsync(HttpClient http, Uri url)
    {
        using var response = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    internal static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    internal static string Flatten(Exception e) => e.InnerException is null ? e.Message : $"{e.Message} -> {Flatten(e.InnerException)}";
}
