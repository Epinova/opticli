using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task The_editor_chooses_the_scopes_on_the_consent_page_and_read_is_required()
    {
        await using var site = await TestSite.StartAsync(o => o.AllowPublish = true);
        var (clientId, _) = await site.RegisterAsync();
        var html = await (await site.Browser("editor").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier())))).Content.ReadAsStringAsync();
        Assert.Equal(["content:write", "content:publish"], Browser.Ticked(html).Select(t => t.Value));
        Assert.Contains("checked disabled> " + Scopes.Describe(Scopes.Read), html);

        var readOnly = await site.ConnectAsync(allow: []);
        Assert.Equal("content:read", readOnly.Scope);
        var noPublish = await site.ConnectAsync(allow: ["content:write"]);
        Assert.Equal("content:read content:write", noPublish.Scope);
        Assert.Contains(site.Audit.Audit, m => m.Contains("authorize allowed") && m.EndsWith("scope content:read content:write", StringComparison.Ordinal));
        var page = await (await site.Browser("editor").GetAsync("/episerver/opticli/connections")).Content.ReadAsStringAsync();
        Assert.Contains("<td>content:read</td>", page);
        Assert.Contains("<td>content:read content:write</td>", page);
    }

    [Fact]
    public async Task A_scope_the_consent_page_did_not_offer_is_ignored_when_posted()
    {
        // The site doesn't allow publishing, so the page doesn't offer it; posting it anyway changes nothing.
        var (clientId, _) = await _site.RegisterAsync();
        var browser = _site.Browser("editor");
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()), scope: "content:read content:write content:publish");
        var html = await (await browser.GetAsync(url)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("content:publish", html);
        var posted = await browser.PostFormAsync(Browser.FormAction(html), [.. Browser.Inputs(html, "allow"), KeyValuePair.Create("scope", "content:publish"), KeyValuePair.Create("scope", "content:write")]);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(posted.Headers.Location!.Query)["code"].ToString();
        var stored = await _site.Store.TakeCodeAsync(Secrets.Hash(code), default);
        Assert.Equal("content:read content:write", stored!.Scope);
    }

    [Fact]
    public async Task A_client_asking_only_for_write_gets_read_with_it()
    {
        Assert.Equal("content:read content:write", Scopes.Grantable("content:write", new OptiCliMcpOptions()));
        Assert.Equal("", Scopes.Grantable("openid profile", new OptiCliMcpOptions()));
        Assert.Equal("content:read", Scopes.Chosen("content:read content:write", []));
        Assert.Equal("content:read", Scopes.Narrowed("content:read content:write", "content:read"));
        Assert.Equal("content:read content:write", Scopes.Narrowed("content:read content:write", null));
        Assert.Equal("", Scopes.Intersect("content:read", ""));
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
        // Also in the page, where a site's own security headers can't replace them.
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src &#39;none&#39;;", html);
        Assert.Contains("form-action &#39;self&#39; http://127.0.0.1:53682\">", html);
        Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\">", html);
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
    public async Task Refresh_tokens_rotate()
    {
        var tokens = await _site.ConnectAsync();
        var first = await _site.TokenAsync(RefreshForm(tokens));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = await Tokens.ReadAsync(first, tokens.ClientId);
        Assert.NotEqual(tokens.Refresh, rotated.Refresh);

        var again = await _site.TokenAsync(RefreshForm(rotated));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("no-store", again.Headers.CacheControl!.ToString());
        Assert.Single(await _site.Store.ListGrantsAsync(null, default));
    }

    [Fact]
    public async Task A_rotated_out_refresh_token_used_again_after_the_grace_period_revokes_the_connection()
    {
        var tokens = await _site.ConnectAsync();
        var rotated = await Tokens.ReadAsync(await _site.TokenAsync(RefreshForm(tokens)), tokens.ClientId);
        _site.Time.Advance(_site.Options.RefreshTokenReuseGrace + TimeSpan.FromSeconds(1));

        // Whoever holds the old token now (the client confused, or someone who copied it): the site can't tell, so the
        // connection ends for the holder of the current token too.
        var reused = await _site.TokenAsync(RefreshForm(tokens));
        Assert.Equal("invalid_grant", await Error(reused));
        Assert.Empty(await _site.Store.ListGrantsAsync(null, default));
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(RefreshForm(rotated))));
        Assert.Null(await _site.Services.GetRequiredService<TokenService>().ValidateAsync(rotated.Access, "http://localhost/episerver/opticli/mcp", default));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("replaced refresh token was used again"));
    }

    [Fact]
    public async Task Within_the_grace_period_the_replaced_token_is_refused_but_the_connection_kept()
    {
        var tokens = await _site.ConnectAsync();
        var rotated = await Tokens.ReadAsync(await _site.TokenAsync(RefreshForm(tokens)), tokens.ClientId);
        _site.Time.Advance(_site.Options.RefreshTokenReuseGrace - TimeSpan.FromSeconds(1));

        // The client retrying a refresh whose answer it never got.
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(RefreshForm(tokens))));
        Assert.Single(await _site.Store.ListGrantsAsync(null, default));
        Assert.NotNull(await _site.Services.GetRequiredService<TokenService>().ValidateAsync(rotated.Access, "http://localhost/episerver/opticli/mcp", default));
        Assert.Equal(HttpStatusCode.OK, (await _site.TokenAsync(RefreshForm(rotated))).StatusCode);
        Assert.Contains(_site.Audit.Audit, m => m.Contains("within the grace period, connection kept"));
    }

    [Fact]
    public async Task Within_the_grace_period_the_replaced_token_from_another_client_still_revokes()
    {
        var tokens = await _site.ConnectAsync();
        await _site.TokenAsync(RefreshForm(tokens));
        var (otherId, _) = await _site.RegisterAsync();

        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(RefreshForm(tokens with { ClientId = otherId }))));
        Assert.Empty(await _site.Store.ListGrantsAsync(null, default));
    }

    [Fact]
    public async Task Without_a_grace_period_every_reuse_revokes()
    {
        await using var site = await TestSite.StartAsync(o => o.RefreshTokenReuseGrace = TimeSpan.Zero);
        var tokens = await site.ConnectAsync();
        await site.TokenAsync(RefreshForm(tokens));
        site.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("invalid_grant", await Error(await site.TokenAsync(RefreshForm(tokens))));
        Assert.Empty(await site.Store.ListGrantsAsync(null, default));
        Assert.Contains("RefreshTokenReuseGrace", new OptiCliMcpOptions { RefreshTokenReuseGrace = TimeSpan.FromMinutes(-1) }.Problem());
    }

    [Fact]
    public async Task Of_parallel_refreshes_with_the_same_token_exactly_one_succeeds_and_its_tokens_keep_working()
    {
        var tokens = await _site.ConnectAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _site.TokenAsync(RefreshForm(tokens)))));

        var winner = await Tokens.ReadAsync(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK), tokens.ClientId);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        // The losers, whether before the rotation (the swap failed) or after it (within the grace period), leave the
        // connection to the winner.
        Assert.Equal(Secrets.Hash(winner.Refresh), Assert.Single(await _site.Store.ListGrantsAsync(null, default)).RefreshHash);
        Assert.NotNull(await _site.Services.GetRequiredService<TokenService>().ValidateAsync(winner.Access, "http://localhost/episerver/opticli/mcp", default));
        Assert.Equal(HttpStatusCode.OK, (await _site.TokenAsync(RefreshForm(winner))).StatusCode);
    }

    [Fact]
    public async Task A_parallel_refresh_that_loses_the_race_does_not_revoke_the_connection()
    {
        var tokens = await _site.ConnectAsync();
        var raced = false;
        // Another refresh with the same token rotates it between this refresh's lookup and its swap.
        _site.Store.BeforeRotate = async () =>
        {
            if (!raced)
            {
                raced = true;
                Assert.Equal(HttpStatusCode.OK, (await _site.TokenAsync(RefreshForm(tokens))).StatusCode);
            }
        };
        var lost = await _site.TokenAsync(RefreshForm(tokens));
        Assert.Equal("invalid_grant", await Error(lost));
        Assert.Single(await _site.Store.ListGrantsAsync(null, default));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("refreshed at the same time, or revoked"));
    }

    [Fact]
    public async Task A_connection_revoked_during_a_refresh_stays_revoked()
    {
        var tokens = await _site.ConnectAsync();
        var grant = Assert.Single(await _site.Store.ListGrantsAsync(null, default));
        _site.Store.BeforeRotate = () => _site.Store.DeleteGrantAsync(grant.GrantId, default);

        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(RefreshForm(tokens))));
        Assert.Empty(await _site.Store.ListGrantsAsync(null, default));
    }

    [Fact]
    public async Task The_store_rotates_only_from_the_current_token_and_never_adds_a_grant()
    {
        var store = new InMemoryOAuthStore();
        var grant = new Grant
        {
            GrantId = "g1", ClientId = "c", ClientName = "C", UserName = "editor", Roles = ["WebEditors"], Scope = "content:read",
            Resource = "r", RefreshHash = "h1", Created = DateTimeOffset.UnixEpoch, Expires = DateTimeOffset.UnixEpoch.AddDays(1),
        };
        await store.AddGrantAsync(grant, default);

        Assert.True(await store.TryRotateRefreshAsync("g1", "h1", "h2", grant.Created, grant.Expires, grant.Roles, grant.Scope, default));
        Assert.False(await store.TryRotateRefreshAsync("g1", "h1", "h3", grant.Created, grant.Expires, grant.Roles, grant.Scope, default));
        var rotated = await store.FindGrantByRefreshAsync("h2", default);
        Assert.Equal("h1", rotated!.PreviousRefreshHash);
        Assert.Equal("g1", (await store.FindGrantByPreviousRefreshAsync("h1", default))!.GrantId);
        Assert.Null(await store.FindGrantByPreviousRefreshAsync("", default));

        await store.DeleteGrantAsync("g1", default);
        Assert.False(await store.TryRotateRefreshAsync("g1", "h2", "h4", grant.Created, grant.Expires, grant.Roles, grant.Scope, default));
        Assert.Null(await store.FindGrantAsync("g1", default));
    }

    [Fact]
    public async Task A_refresh_for_fewer_scopes_gets_an_access_token_for_those_and_the_connection_keeps_the_rest()
    {
        var tokens = await _site.ConnectAsync();
        var form = RefreshForm(tokens);
        form["scope"] = "content:read";
        var narrowed = await Tokens.ReadAsync(await _site.TokenAsync(form), tokens.ClientId);
        Assert.Equal("content:read", narrowed.Scope);
        var principal = await _site.Services.GetRequiredService<TokenService>().ValidateAsync(narrowed.Access, "http://localhost/episerver/opticli/mcp", default);
        Assert.Equal("content:read", principal!.FindFirst(McpClaims.Scope)!.Value);
        Assert.Equal("content:read content:write", Assert.Single(await _site.Store.ListGrantsAsync(null, default)).Scope);

        var full = await Tokens.ReadAsync(await _site.TokenAsync(RefreshForm(narrowed)), tokens.ClientId);
        Assert.Equal("content:read content:write", full.Scope);
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
    public async Task A_refresh_after_the_editors_account_was_disabled_or_deleted_deletes_the_grant()
    {
        _site.Roles.Set("disabled", "WebEditors");
        _site.Roles.Set("deleted", "WebEditors");
        var disabled = await _site.ConnectAsync("disabled");
        var deleted = await _site.ConnectAsync("deleted");
        _site.Roles.Disable("disabled");
        _site.Roles.Delete("deleted");

        var refused = await _site.TokenAsync(RefreshForm(disabled));
        Assert.Equal("invalid_grant", await Error(refused));
        Assert.Contains("disabled", await refused.Content.ReadAsStringAsync());
        Assert.Equal("invalid_grant", await Error(await _site.TokenAsync(RefreshForm(deleted))));
        Assert.Empty(await _site.Store.ListGrantsAsync(null, default));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("refused: account disabled") && m.Contains("disabled"));
    }

    [Fact]
    public async Task A_disabled_account_can_not_connect_though_its_login_still_works()
    {
        _site.Roles.Set("locked", "WebEditors");
        _site.Roles.Disable("locked");
        var (clientId, _) = await _site.RegisterAsync();
        var response = await _site.Browser("locked").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier())));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("is disabled on this site", await response.Content.ReadAsStringAsync());
        Assert.Contains(_site.Audit.Audit, m => m.Contains("refused (account disabled)") && m.Contains("locked"));
    }

    [Fact]
    public async Task When_the_user_store_cannot_say_the_roles_decide()
    {
        var tokens = await _site.ConnectAsync();
        _site.Roles.Unavailable = true;
        // The roles fall back to the grant's, and the account can't be checked: the refresh goes ahead.
        Assert.Equal(HttpStatusCode.OK, (await _site.TokenAsync(RefreshForm(tokens))).StatusCode);
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
    public async Task A_connection_ends_after_ConnectionLifetime_however_often_it_is_refreshed()
    {
        await using var site = await TestSite.StartAsync(o => o.ConnectionLifetime = TimeSpan.FromDays(3));
        var tokens = await site.ConnectAsync();
        for (var day = 1; day < 3; day++)
        {
            site.Time.Advance(TimeSpan.FromDays(1));
            var refreshed = await site.TokenAsync(RefreshForm(tokens));
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            tokens = await Tokens.ReadAsync(refreshed, tokens.ClientId);
            // The connection's end, not RefreshTokenLifetime from now: an unused connection lapses no later either.
            Assert.Equal(TimeSpan.FromDays(3 - day), Assert.Single(await site.Store.ListGrantsAsync(null, default)).Expires - site.Time.GetUtcNow());
        }
        site.Time.Advance(TimeSpan.FromDays(1));

        var refused = await site.TokenAsync(RefreshForm(tokens));
        Assert.Equal("invalid_grant", await Error(refused));
        Assert.Contains("connect again", await refused.Content.ReadAsStringAsync());
        Assert.Empty(await site.Store.ListGrantsAsync(null, default));
        Assert.Contains(site.Audit.Audit, m => m.Contains("ConnectionLifetime"));
    }

    [Fact]
    public async Task An_access_token_never_outlasts_its_connection()
    {
        await using var site = await TestSite.StartAsync(o => o.ConnectionLifetime = TimeSpan.FromMinutes(90));
        var tokens = await site.ConnectAsync();
        Assert.Equal(3600, tokens.ExpiresIn);
        site.Time.Advance(TimeSpan.FromMinutes(50));

        // 40 minutes left of the connection: the access token says so, and stops then.
        var refreshed = await Tokens.ReadAsync(await site.TokenAsync(RefreshForm(tokens)), tokens.ClientId);
        Assert.Equal(40 * 60, refreshed.ExpiresIn);
        var service = site.Services.GetRequiredService<TokenService>();
        Assert.NotNull(await service.ValidateAsync(refreshed.Access, "http://localhost/episerver/opticli/mcp", default));
        site.Time.Advance(TimeSpan.FromMinutes(40));
        Assert.Null(await service.ValidateAsync(refreshed.Access, "http://localhost/episerver/opticli/mcp", default));
        Assert.Equal("invalid_grant", await Error(await site.TokenAsync(RefreshForm(refreshed))));
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
    public async Task Registered_clients_left_unused_for_30_days_are_deleted_and_used_ones_kept()
    {
        // A connection may outlast its client's first 30 days only on a site that lets connections last longer.
        await using var site = await TestSite.StartAsync(o => o.ConnectionLifetime = TimeSpan.FromDays(60));
        var (unused, _) = await site.RegisterAsync();
        var connected = await site.ConnectAsync();
        site.Time.Advance(TimeSpan.FromDays(29));
        await site.TokenAsync(RefreshForm(connected)); // the connection stays in use
        site.Time.Advance(TimeSpan.FromDays(2));
        var (recent, _) = await site.RegisterAsync();
        Assert.Equal(3, site.Store.ClientCount);

        await site.ConnectAsync(); // a token issue runs the cleanup

        Assert.Null(await site.Store.FindClientAsync(unused, default));
        Assert.NotNull(await site.Store.FindClientAsync(connected.ClientId, default));
        Assert.NotNull(await site.Store.FindClientAsync(recent, default));
    }

    [Fact]
    public async Task A_client_whose_only_connection_reached_ConnectionLifetime_is_deleted_like_an_unused_one()
    {
        var connected = await _site.ConnectAsync();
        _site.Time.Advance(TimeSpan.FromDays(29));
        await _site.TokenAsync(RefreshForm(connected));
        _site.Time.Advance(TimeSpan.FromDays(2));

        await _site.ConnectAsync();

        Assert.Null(await _site.Store.FindClientAsync(connected.ClientId, default));
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
