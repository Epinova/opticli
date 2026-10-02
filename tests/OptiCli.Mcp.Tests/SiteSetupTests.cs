using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests;

public class RateLimitTests
{
    [Fact]
    public async Task Registration_is_limited_per_address_with_a_429()
    {
        await using var site = await TestSite.StartAsync(limits: new OAuthRateLimits { RegisterPerMinute = 2 });
        var body = new { redirect_uris = new[] { TestSite.RedirectUri }, token_endpoint_auth_method = "none" };
        Assert.Equal(HttpStatusCode.Created, (await site.Client().PostAsync("/episerver/opticli/oauth/register", TestSite.Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await site.Client().PostAsync("/episerver/opticli/oauth/register", TestSite.Json(body))).StatusCode);
        var limited = await site.Client().PostAsync("/episerver/opticli/oauth/register", TestSite.Json(body));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task The_token_endpoint_is_limited_on_its_own()
    {
        await using var site = await TestSite.StartAsync(limits: new OAuthRateLimits { TokenPerMinute = 3 });
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "guess", ["client_id"] = "mcp_x" };
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await site.TokenAsync(form)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await site.TokenAsync(form)).StatusCode);
        // Registration still has its own window.
        await site.RegisterAsync();
    }

    [Fact]
    public async Task The_consent_post_is_limited()
    {
        await using var site = await TestSite.StartAsync(limits: new OAuthRateLimits { AuthorizePerMinute = 1 });
        var (clientId, _) = await site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));
        Assert.Equal(HttpStatusCode.OK, (await site.Browser("editor").GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await site.Browser("editor").PostFormAsync(url, new() { ["decision"] = "allow" })).StatusCode);
    }

    [Fact]
    public async Task A_window_replenishes()
    {
        using var limiter = new OAuthRateLimiter(new OAuthRateLimits { RegisterPerMinute = 1, Window = TimeSpan.FromMilliseconds(200) });
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        Assert.Null(limiter.Check(OAuthRateLimiter.Register, context));
        Assert.NotNull(limiter.Check(OAuthRateLimiter.Register, context));
        var other = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        other.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.8");
        Assert.Null(limiter.Check(OAuthRateLimiter.Register, other));
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Null(limiter.Check(OAuthRateLimiter.Register, context));
    }
}

public sealed class ConnectionsPageTests : IAsyncLifetime
{
    private TestSite _site = null!;

    public async Task InitializeAsync() => _site = await TestSite.StartAsync();

    public async Task DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Someone_not_signed_in_is_sent_to_the_login()
    {
        var response = await _site.Browser(null).GetAsync("/episerver/opticli/connections");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task An_editor_sees_only_their_own_connections_with_their_last_use()
    {
        _site.Roles.Set("other", "WebEditors");
        var mine = await _site.ConnectAsync("editor");
        await _site.ConnectAsync("other");
        await _site.Services.GetRequiredService<TokenService>().ValidateAsync(mine.Access, "http://localhost/episerver/opticli/mcp", default);

        var response = await _site.Browser("editor").GetAsync("/episerver/opticli/connections");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "Revoke</button>"));
        Assert.Contains("Test client", html);
        Assert.Contains("content:read content:write", html);
        Assert.Contains("2026-10-01 12:00 UTC", html);
        Assert.DoesNotContain(">other<", html);
    }

    [Fact]
    public async Task An_administrator_sees_and_revokes_everyones()
    {
        _site.Roles.Set("other", "WebEditors");
        await _site.ConnectAsync("editor");
        await _site.ConnectAsync("other");
        var browser = _site.Browser("admin");
        var html = await (await browser.GetAsync("/episerver/opticli/connections")).Content.ReadAsStringAsync();
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "Revoke</button>").Count);
        Assert.Contains("<td>other</td>", html);

        var form = Browser.Inputs(html);
        Assert.Equal(HttpStatusCode.SeeOther, (await browser.PostFormAsync("/episerver/opticli/connections", form)).StatusCode);
        Assert.Single(await _site.Store.ListGrantsAsync(null, default));
        Assert.Contains(_site.Audit.Audit, m => m.Contains("revoked by admin"));
    }

    [Fact]
    public async Task An_editor_cannot_revoke_someone_elses()
    {
        _site.Roles.Set("other", "WebEditors");
        await _site.ConnectAsync("editor"); // so the editor's page has a form, and an antiforgery token, to tamper with
        await _site.ConnectAsync("other");
        var theirs = Assert.Single(await _site.Store.ListGrantsAsync("other", default));
        var browser = _site.Browser("editor");
        var form = Browser.Inputs(await (await browser.GetAsync("/episerver/opticli/connections")).Content.ReadAsStringAsync());
        form["grant"] = theirs.GrantId;
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostFormAsync("/episerver/opticli/connections", form)).StatusCode);
        Assert.Single(await _site.Store.ListGrantsAsync("other", default));
    }

    [Fact]
    public async Task A_revoke_without_the_antiforgery_token_is_refused()
    {
        await _site.ConnectAsync("editor");
        var grant = Assert.Single(await _site.Store.ListGrantsAsync(null, default));
        var response = await _site.Browser("editor").PostFormAsync("/episerver/opticli/connections", new() { ["grant"] = grant.GrantId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await _site.Store.ListGrantsAsync(null, default));
    }
}

public class OptionsTests
{
    [Fact]
    public async Task The_defaults_are_safe()
    {
        await using var site = await TestSite.StartAsync();
        var options = site.Options;
        Assert.Equal("/episerver/opticli", options.BasePath);
        Assert.False(options.AllowPublish);
        Assert.False(options.AllowDelete);
        Assert.Equal(10 * 1024 * 1024, options.MaxUploadBytes);
        Assert.Equal(TimeSpan.FromHours(1), options.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(30), options.RefreshTokenLifetime);
        Assert.Equal(new[] { "WebEditors", "WebAdmins", "CmsEditors", "CmsAdmins", "Administrators" }, options.AllowedRoles);
    }

    [Fact]
    public async Task Options_bind_from_the_configuration_section_and_code_wins()
    {
        await using var site = await TestSite.StartAsync(
            o => o.AllowDelete = false,
            new()
            {
                ["OptiCli:Mcp:AllowPublish"] = "true",
                ["OptiCli:Mcp:AllowDelete"] = "true",
                ["OptiCli:Mcp:MaxUploadBytes"] = "1024",
                ["OptiCli:Mcp:AccessTokenLifetime"] = "00:15:00",
                ["OptiCli:Mcp:AllowedRoles:0"] = "WebAdmins",
            });
        var options = site.Options;
        Assert.True(options.AllowPublish);
        Assert.False(options.AllowDelete);
        Assert.Equal(1024, options.MaxUploadBytes);
        Assert.Equal(TimeSpan.FromMinutes(15), options.AccessTokenLifetime);
        // A list in configuration replaces the default one: a site can narrow who may connect.
        Assert.Equal(new[] { "WebAdmins" }, options.AllowedRoles);
    }

    [Fact]
    public async Task With_publishing_allowed_the_publish_scope_is_offered()
    {
        await using var site = await TestSite.StartAsync(o => o.AllowPublish = true);
        var tokens = await site.ConnectAsync();
        Assert.Equal("content:read content:write content:publish", tokens.Scope);
    }

    [Fact]
    public async Task Allowed_roles_decide_who_may_connect()
    {
        await using var site = await TestSite.StartAsync(o => o.AllowedRoles = ["ProductEditors"]);
        site.Roles.Set("product", "ProductEditors");
        var (clientId, _) = await site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));
        Assert.Equal(HttpStatusCode.Forbidden, (await site.Browser("editor").GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Browser("product").GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("episerver/opticli")]
    [InlineData("/episerver/opticli/")]
    [InlineData("/a b")]
    public async Task A_bad_base_path_fails_at_startup(string basePath)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => TestSite.StartAsync(o => o.BasePath = basePath));
        Assert.Contains("BasePath", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50L * 1024 * 1024 + 1)]
    public async Task An_upload_size_out_of_range_fails_at_startup(long bytes)
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => TestSite.StartAsync(o => o.MaxUploadBytes = bytes));
        Assert.Contains("MaxUploadBytes", error.Message);
    }

    [Fact]
    public async Task The_site_root_needs_a_host_of_its_own()
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => TestSite.StartAsync(o => o.BasePath = ""));
        Assert.Contains("RequireHost", error.Message);
    }

    [Fact]
    public async Task Required_host_hides_the_module_on_other_hosts()
    {
        await using var site = await TestSite.StartAsync(o => o.RequireHost = "editors.example");
        Assert.Equal(HttpStatusCode.NotFound,
            (await site.Client().GetAsync("/.well-known/oauth-authorization-server/episerver/opticli")).StatusCode);
        var response = await site.Client("editors.example").GetAsync("/.well-known/oauth-authorization-server/episerver/opticli");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client().PostAsync("/episerver/opticli/mcp", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task On_a_dedicated_host_the_issuer_is_the_root_and_the_root_documents_are_the_modules()
    {
        await using var site = await TestSite.StartAsync(o =>
        {
            o.BasePath = "";
            o.RequireHost = "mcp.example";
        });
        var client = site.Client("mcp.example");
        foreach (var path in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" })
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(path));
            Assert.Equal("http://mcp.example", document.RootElement.GetProperty("issuer").GetString());
            Assert.Equal("http://mcp.example/oauth/token", document.RootElement.GetProperty("token_endpoint").GetString());
        }
        foreach (var path in new[] { "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-protected-resource" })
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(path));
            Assert.Equal("http://mcp.example/mcp", document.RootElement.GetProperty("resource").GetString());
            Assert.Equal("http://mcp.example", document.RootElement.GetProperty("authorization_servers")[0].GetString());
        }
        var unauthorized = await client.PostAsync("/mcp", TestSite.Json(new { jsonrpc = "2.0", id = 1, method = "ping" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains("resource_metadata=\"http://mcp.example/.well-known/oauth-protected-resource/mcp\"", unauthorized.Headers.WwwAuthenticate.ToString());
        // The site's public host doesn't see any of it.
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client().GetAsync("/.well-known/openid-configuration")).StatusCode);
    }

    [Fact]
    public async Task A_grant_with_publish_loses_it_at_refresh_when_the_site_stops_allowing_publishing()
    {
        // Same store, the site restarted without AllowPublish.
        await using var site = await TestSite.StartAsync(o => o.AllowPublish = true);
        var tokens = await site.ConnectAsync();
        var grant = Assert.Single(await site.Store.ListGrantsAsync(null, default));
        await using var restarted = await TestSite.StartAsync(services: s => s.AddSingleton<IOAuthStore>(site.Store));
        var response = await restarted.TokenAsync(AuthorizationServerTests.RefreshForm(tokens));
        Assert.Equal("content:read content:write", (await Tokens.ReadAsync(response, tokens.ClientId)).Scope);
        Assert.Equal("content:read content:write content:publish", grant.Scope);
    }

    [Fact]
    public void Calling_AddOptiCliMcp_twice_is_an_error()
    {
        var services = new ServiceCollection();
        services.AddOptiCliMcp();
        Assert.Throws<InvalidOperationException>(() => services.AddOptiCliMcp());
    }
}
