using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests;

/// <summary>
/// A site with custom error pages (<c>UseStatusCodePagesWithReExecute</c>, to a GET-only error page): the module's
/// errors reach the client as the module wrote them, never as the site's page or the 405 a re-executed POST gets.
/// </summary>
public sealed class StatusCodePagesTests : IAsyncLifetime
{
    private const string Mcp = "/episerver/opticli/mcp";

    private TestSite _site = null!;

    public async Task InitializeAsync() => _site = await TestSite.StartAsync(statusCodePages: true);

    public async Task DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_site_rewrites_its_own_errors_without_a_body()
    {
        // What the module's errors would get: a GET shows the site's page, a POST is re-executed into a 405.
        var get = await _site.Client().GetAsync("/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Contains("Site error page 404", await get.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _site.Client().PostAsync("/no-such-page", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_401_keeps_its_challenge_and_has_a_json_body()
    {
        var response = await _site.Client().PostAsync(Mcp, Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/episerver/opticli/mcp\"", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unauthorized", await AuthorizationServerTests.Error(response));
    }

    [Fact]
    public async Task A_bad_token_gets_its_401_too()
    {
        var response = await Call("oc_forged", Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("error=\"invalid_token\"", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.Equal("invalid_token", await AuthorizationServerTests.Error(response));
    }

    [Fact]
    public async Task A_token_without_content_read_gets_its_403()
    {
        var grant = new Grant
        {
            GrantId = "write-only", ClientId = "mcp_x", ClientName = "X", UserName = "editor", Roles = ["WebEditors"], Scope = "content:write",
            Resource = "http://localhost/episerver/opticli/mcp", RefreshHash = "x", Created = _site.Time.GetUtcNow(), Expires = _site.Time.GetUtcNow().AddDays(1),
        };
        await _site.Store.AddGrantAsync(grant, default);
        var (access, _) = _site.Services.GetRequiredService<TokenService>().Issue(grant);
        var response = await Call(access, Initialize());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("error=\"insufficient_scope\"", response.Headers.WwwAuthenticate.Single().ToString());
        Assert.Equal("insufficient_scope", await AuthorizationServerTests.Error(response));
    }

    [Fact]
    public async Task A_request_too_large_gets_its_413()
    {
        await using var site = await TestSite.StartAsync(o => o.MaxUploadBytes = 3, statusCodePages: true);
        var tokens = await site.ConnectAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, Mcp) { Content = new StringContent(new string(' ', (int)McpRequestLimit.Bytes(site.Options) + 1), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        var response = await site.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("larger than this site accepts", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_method_the_endpoint_does_not_take_keeps_its_405()
    {
        // The stateless endpoint has no GET; routing's own 405 isn't the module's, and the site's page may show for
        // it, but a client still gets the 405 it needs to know there is no event stream.
        var tokens = await _site.ConnectAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, Mcp);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        request.Headers.Accept.ParseAdd("text/event-stream");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _site.Client().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task OAuth_errors_and_rate_limits_keep_their_json()
    {
        await using var site = await TestSite.StartAsync(limits: new OptiCliMcpRateLimits { TokenPerAddressPerMinute = 1 }, statusCodePages: true);
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "ocr_nothing", ["client_id"] = "mcp_nobody" };
        var first = await site.TokenAsync(form);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal("invalid_client", await AuthorizationServerTests.Error(first));
        var limited = await site.TokenAsync(form);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("slow_down", await AuthorizationServerTests.Error(limited));
    }

    [Fact]
    public async Task The_module_pages_keep_their_own_error_pages()
    {
        var response = await _site.Browser("visitor").GetAsync(TestSite.AuthorizeUrl((await _site.RegisterAsync()).ClientId, Pkce.Challenge(TestSite.Verifier())));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No access", html);
        Assert.DoesNotContain("Site error page", html);
    }

    private async Task<HttpResponseMessage> Call(string token, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Mcp) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await _site.Client().SendAsync(request);
    }

    private static StringContent Initialize() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
        Encoding.UTF8,
        "application/json");
}
