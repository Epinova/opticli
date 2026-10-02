using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests.OAuth;

/// <summary>The authorization server's endpoints on a test site: registration, the consent page, and the token endpoint.</summary>
public sealed class AuthorizationServerTests : IAsyncLifetime
{
    private TestSite _site = null!;

    public async Task InitializeAsync() => _site = await TestSite.StartAsync();

    public async Task DisposeAsync() => await _site.DisposeAsync();

    // --- Registration --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_public_client_registers_without_a_secret()
    {
        var (clientId, secret) = await _site.RegisterAsync();
        Assert.StartsWith("mcp_", clientId);
        Assert.Null(secret);
        var stored = await _site.Store.FindClientAsync(clientId, default);
        Assert.Equal(ClientAuthMethods.None, stored!.AuthMethod);
    }

    [Fact]
    public async Task A_confidential_client_gets_a_secret_that_is_stored_only_as_a_hash()
    {
        var (clientId, secret) = await _site.RegisterAsync(ClientAuthMethods.SecretPost);
        Assert.StartsWith("ocs_", secret);
        var stored = await _site.Store.FindClientAsync(clientId, default);
        Assert.Equal(Secrets.Hash(secret!), stored!.SecretHash);
        Assert.DoesNotContain(secret!, stored.SecretHash);
    }

    [Theory]
    [InlineData("""{"redirect_uris":["http://evil.example/cb"]}""", "invalid_redirect_uri")]
    [InlineData("""{"redirect_uris":[]}""", "invalid_redirect_uri")]
    [InlineData("""{"client_name":"No redirects"}""", "invalid_redirect_uri")]
    [InlineData("""{"redirect_uris":["https://ok.example/cb"],"token_endpoint_auth_method":"private_key_jwt"}""", "invalid_client_metadata")]
    [InlineData("""{"redirect_uris":["https://ok.example/cb"],"grant_types":["client_credentials"]}""", "invalid_client_metadata")]
    [InlineData("""not json""", "invalid_client_metadata")]
    public async Task Bad_registrations_are_refused(string body, string error)
    {
        var response = await _site.Client().PostAsync("/episerver/opticli/oauth/register", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await Error(response));
    }

    [Fact]
    public async Task A_huge_registration_is_refused()
    {
        var body = $$"""{"redirect_uris":["https://ok.example/cb"],"client_uri":"{{new string('x', 20_000)}}"}""";
        var response = await _site.Client().PostAsync("/episerver/opticli/oauth/register", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Authorize -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_client_gets_an_error_page_not_a_redirect()
    {
        var response = await _site.Browser("editor").GetAsync(TestSite.AuthorizeUrl("mcp_unknown", Pkce.Challenge(TestSite.Verifier())));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains("Unknown app", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://127.0.0.1:53682/other")]
    [InlineData("")]
    public async Task An_unregistered_redirect_gets_an_error_page_not_a_redirect(string redirect)
    {
        var (clientId, _) = await _site.RegisterAsync();
        var response = await _site.Browser("editor").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()), redirect));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_loopback_redirect_may_use_another_port()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var response = await _site.Browser("editor").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()), "http://127.0.0.1:61000/callback"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_request_without_pkce_goes_back_to_the_client_with_an_error()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, "plain-challenge");
        var response = await _site.Browser("editor").GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(TestSite.RedirectUri + "?error=invalid_request", location);
        Assert.Contains("state=xyz", location);
        Assert.Contains("iss=http%3A%2F%2Flocalhost%2Fepiserver%2Fopticli", location);
    }

    [Fact]
    public async Task A_request_for_another_resource_is_refused()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var response = await _site.Browser("editor").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()), resource: "https://other.example/mcp"));
        Assert.Contains("error=invalid_target", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Someone_not_signed_in_is_sent_to_the_sites_own_login_and_back()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));
        var response = await _site.Browser(null).GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?ReturnUrl=" + Uri.EscapeDataString(url), response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_consent_page_names_the_client_the_redirect_host_the_user_and_the_scopes()
    {
        var (clientId, _) = await _site.RegisterAsync(name: "Claude <script>");
        var response = await _site.Browser("editor").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier())));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Connect Claude &lt;script&gt;?", html);
        Assert.Contains("<b>editor</b>", html);
        Assert.Contains("<b>127.0.0.1:53682</b>", html);
        Assert.Contains(Scopes.Describe(Scopes.Read), html);
        Assert.Contains(Scopes.Describe(Scopes.Write), html);
        Assert.DoesNotContain(Scopes.Describe(Scopes.Publish), html);
    }

    [Fact]
    public async Task The_consent_page_cannot_be_framed_and_posts_only_to_itself_or_the_client()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var response = await _site.Browser("editor").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier())));
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("form-action 'self' http://127.0.0.1:53682", csp);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task A_consent_post_without_the_antiforgery_token_is_refused()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));
        var browser = _site.Browser("editor");
        await browser.GetAsync(url);
        var response = await browser.PostFormAsync(url, new() { ["decision"] = "allow" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(0, _site.Store.CodeCount);
    }

    [Fact]
    public async Task Another_users_antiforgery_token_is_refused()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));
        var html = await (await _site.Browser("admin").GetAsync(url)).Content.ReadAsStringAsync();
        var response = await _site.Browser("editor").PostFormAsync(url, Browser.Inputs(html, "allow"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_role_gate_uses_the_current_roles_not_the_login_cookies()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));
        // The login still says WebEditors, but the role store no longer does.
        _site.Roles.Set("former", []);
        var response = await _site.Browser("former", cookieRoles: "WebEditors").GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("an editor on this site", await response.Content.ReadAsStringAsync());
        Assert.Contains(_site.Audit.Audit, m => m.Contains("refused (not an editor)") && m.Contains("former"));
    }

    [Fact]
    public async Task Virtual_roles_count_for_the_gate()
    {
        _site.Roles.Set("admin2", "Administrators");
        var (clientId, _) = await _site.RegisterAsync();
        var response = await _site.Browser("admin2").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier())));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Denying_goes_back_to_the_client_with_access_denied()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var back = await _site.AuthorizeAsync(clientId, TestSite.Verifier(), decision: "deny");
        Assert.Equal("access_denied", back["error"]);
        Assert.Equal("xyz", back["state"]);
        Assert.False(back.ContainsKey("code"));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("authorize denied"));
    }

    [Fact]
    public async Task Allowing_gives_a_code_with_state_and_issuer_and_stores_only_its_hash()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var back = await _site.AuthorizeAsync(clientId, TestSite.Verifier());
        Assert.Equal("xyz", back["state"]);
        Assert.Equal("http://localhost/episerver/opticli", back["iss"]);
        Assert.NotNull(await _site.Store.TakeCodeAsync(Secrets.Hash(back["code"]), default));
    }

    // --- Token ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_code_gives_tokens_once()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var verifier = TestSite.Verifier();
        var back = await _site.AuthorizeAsync(clientId, verifier);
        var form = CodeForm(clientId, back["code"], verifier);

        var first = await _site.TokenAsync(form);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("no-store", first.Headers.CacheControl!.ToString());
        var tokens = await Tokens.ReadAsync(first, clientId);
        Assert.StartsWith("oc_", tokens.Access);
        Assert.StartsWith("ocr_", tokens.Refresh);
        Assert.Equal("content:read content:write", tokens.Scope);

        var second = await _site.TokenAsync(form);
        Assert.Equal("invalid_grant", await Error(second));
    }

    [Fact]
    public async Task A_wrong_verifier_fails_and_uses_up_the_code()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var verifier = TestSite.Verifier();
        var back = await _site.AuthorizeAsync(clientId, verifier);
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(CodeForm(clientId, back["code"], TestSite.Verifier()))));
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(CodeForm(clientId, back["code"], verifier))));
    }

    [Fact]
    public async Task A_code_expires_after_5_minutes()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var verifier = TestSite.Verifier();
        var back = await _site.AuthorizeAsync(clientId, verifier);
        _site.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(CodeForm(clientId, back["code"], verifier))));
    }

    [Fact]
    public async Task A_code_works_only_for_its_client_and_redirect()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var (otherId, _) = await _site.RegisterAsync();
        var verifier = TestSite.Verifier();
        var back = await _site.AuthorizeAsync(clientId, verifier);
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(CodeForm(otherId, back["code"], verifier))));

        back = await _site.AuthorizeAsync(clientId, verifier);
        var form = CodeForm(clientId, back["code"], verifier);
        form["redirect_uri"] = "http://127.0.0.1:1/callback";
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(form)));
    }

    [Fact]
    public async Task A_code_for_another_resource_is_refused()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var verifier = TestSite.Verifier();
        var back = await _site.AuthorizeAsync(clientId, verifier);
        var form = CodeForm(clientId, back["code"], verifier);
        form["resource"] = "https://other.example/mcp";
        Assert.Equal("invalid_target", await Error(await _site.TokenAsync(form)));
    }

    [Fact]
    public async Task A_confidential_client_must_authenticate_with_its_registered_method()
    {
        var (clientId, secret) = await _site.RegisterAsync(ClientAuthMethods.SecretBasic);
        var verifier = TestSite.Verifier();

        // No secret at all.
        var back = await _site.AuthorizeAsync(clientId, verifier);
        var response = await _site.TokenAsync(CodeForm(clientId, back["code"], verifier));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_client", await Error(response));

        // The right secret, but posted rather than in the Basic header it registered.
        var form = CodeForm(clientId, back["code"], verifier);
        form["client_secret"] = secret!;
        Assert.Equal("invalid_client", await Error(await _site.TokenAsync(form)));

        // A wrong secret in the header.
        var wrong = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:ocs_wrong"));
        response = await _site.TokenAsync(CodeForm(null, back["code"], verifier), wrong);
        Assert.Equal("invalid_client", await Error(response));
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());

        // The right secret in the header: the code is still unused, since the client never got that far.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"));
        Assert.Equal(HttpStatusCode.OK, (await _site.TokenAsync(CodeForm(null, back["code"], verifier), basic)).StatusCode);
    }

    [Fact]
    public async Task A_public_client_may_not_send_a_secret()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var verifier = TestSite.Verifier();
        var back = await _site.AuthorizeAsync(clientId, verifier);
        var form = CodeForm(clientId, back["code"], verifier);
        form["client_secret"] = "anything";
        Assert.Equal("invalid_client", await Error(await _site.TokenAsync(form)));
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_an_old_one_fails()
    {
        var tokens = await _site.ConnectAsync();
        var first = await _site.TokenAsync(RefreshForm(tokens));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = await Tokens.ReadAsync(first, tokens.ClientId);
        Assert.NotEqual(tokens.Refresh, rotated.Refresh);

        var reused = await _site.TokenAsync(RefreshForm(tokens));
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal("invalid_grant", await Error(reused));
        Assert.Contains("no-store", reused.Headers.CacheControl!.ToString());

        Assert.Equal(HttpStatusCode.OK, (await _site.TokenAsync(RefreshForm(rotated))).StatusCode);
        Assert.Single(await _site.Store.ListGrantsAsync(null, default));
    }

    [Fact]
    public async Task A_refresh_token_works_only_for_its_client()
    {
        var tokens = await _site.ConnectAsync();
        var (otherId, _) = await _site.RegisterAsync();
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(RefreshForm(tokens with { ClientId = otherId }))));
    }

    [Fact]
    public async Task A_refresh_after_the_editor_lost_their_role_deletes_the_grant()
    {
        _site.Roles.Set("leaving", "WebEditors");
        var tokens = await _site.ConnectAsync("leaving");
        _site.Roles.Set("leaving");
        var response = await _site.TokenAsync(RefreshForm(tokens));
        Assert.Equal("invalid_grant", await Error(response));
        Assert.Empty(await _site.Store.ListGrantsAsync(null, default));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("no longer an editor"));
    }

    [Fact]
    public async Task A_refresh_picks_up_the_editors_current_roles()
    {
        _site.Roles.Set("growing", "WebEditors");
        var tokens = await _site.ConnectAsync("growing");
        _site.Roles.Set("growing", "WebEditors", "ProductEditors");
        await _site.TokenAsync(RefreshForm(tokens));
        var grant = Assert.Single(await _site.Store.ListGrantsAsync("growing", default));
        Assert.Equal(new[] { "WebEditors", "ProductEditors" }, grant.Roles);
    }

    [Fact]
    public async Task A_refresh_token_expires_unless_used()
    {
        var tokens = await _site.ConnectAsync();
        _site.Time.Advance(TimeSpan.FromDays(30));
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(RefreshForm(tokens))));
    }

    [Fact]
    public async Task Expired_codes_and_grants_are_cleaned_up_when_tokens_are_issued()
    {
        var (clientId, _) = await _site.RegisterAsync();
        await _site.AuthorizeAsync(clientId, TestSite.Verifier()); // a code never exchanged
        await _site.ConnectAsync();
        _site.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, _site.Store.CodeCount);
        await _site.ConnectAsync();
        Assert.Equal(0, _site.Store.CodeCount);
        Assert.Single(await _site.Store.ListGrantsAsync(null, default));
    }

    [Fact]
    public async Task Unknown_grant_types_and_non_form_bodies_are_refused()
    {
        Assert.Equal("unsupported_grant_type", await Error(await _site.TokenAsync(new() { ["grant_type"] = "password", ["client_id"] = (await _site.RegisterAsync()).ClientId })));
        var json = await _site.Client().PostAsync("/episerver/opticli/oauth/token", TestSite.Json(new { grant_type = "refresh_token" }));
        Assert.Equal("invalid_request", await Error(json));
    }

    [Fact]
    public async Task The_audit_log_records_issues_and_refreshes_but_never_secrets()
    {
        var tokens = await _site.ConnectAsync();
        var refreshed = await Tokens.ReadAsync(await _site.TokenAsync(RefreshForm(tokens)), tokens.ClientId);
        Assert.Contains(_site.Audit.Audit, m => m.Contains("authorize allowed") && m.Contains("editor"));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("token issued"));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("refresh rotated"));
        foreach (var secret in new[] { tokens.Access, tokens.Refresh, refreshed.Access, refreshed.Refresh })
        {
            Assert.DoesNotContain(secret, _site.Audit.All);
        }
    }

    internal static Dictionary<string, string> CodeForm(string? clientId, string code, string verifier)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = TestSite.RedirectUri,
            ["code_verifier"] = verifier,
        };
        if (clientId is not null)
        {
            form["client_id"] = clientId;
        }
        return form;
    }

    internal static Dictionary<string, string> RefreshForm(Tokens tokens) => new()
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = tokens.Refresh,
        ["client_id"] = tokens.ClientId,
    };

    internal static async Task<string?> Error(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }
}
