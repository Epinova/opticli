using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests;

/// <summary>The MCP endpoint, its metadata documents and the 401 that leads a client to them.</summary>
public sealed class McpEndpointTests : IAsyncLifetime
{
    private TestSite _site = null!;

    public async Task InitializeAsync() => _site = await TestSite.StartAsync();

    public async Task DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Without_a_token_the_endpoint_answers_401_pointing_to_the_resource_metadata()
    {
        var response = await _site.Client().PostAsync("/episerver/opticli/mcp", Initialize());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var challenge = response.Headers.WwwAuthenticate.Single().ToString();
        Assert.Equal(
            "Bearer resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/episerver/opticli/mcp\", scope=\"content:read content:write\"",
            challenge);
    }

    [Fact]
    public async Task A_bad_token_answers_401_with_invalid_token()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/episerver/opticli/mcp") { Content = Initialize() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "oc_forged");
        var response = await _site.Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("error=\"invalid_token\"", response.Headers.WwwAuthenticate.Single().ToString());
    }

    [Fact]
    public async Task The_sites_login_cookie_does_not_open_the_mcp_endpoint()
    {
        var response = await _site.Browser("editor").SendAsync(new HttpRequestMessage(HttpMethod.Post, "/episerver/opticli/mcp") { Content = Initialize() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_content_read_answers_403_insufficient_scope()
    {
        // The consent page always grants read with anything else, so such a token comes from a grant made by hand.
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
    }

    [Fact]
    public async Task The_protected_resource_metadata_names_the_resource_and_its_issuer()
    {
        var root = await Json("/.well-known/oauth-protected-resource/episerver/opticli/mcp");
        Assert.Equal("http://localhost/episerver/opticli/mcp", root.GetProperty("resource").GetString());
        Assert.Equal("http://localhost/episerver/opticli", root.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal(new[] { "content:read", "content:write" }, root.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()));
    }

    [Theory]
    [InlineData("/.well-known/oauth-authorization-server/episerver/opticli")]
    [InlineData("/.well-known/openid-configuration/episerver/opticli")]
    [InlineData("/episerver/opticli/.well-known/openid-configuration")]
    public async Task The_authorization_server_metadata_is_served_on_every_discovery_path(string path)
    {
        var root = await Json(path);
        Assert.Equal("http://localhost/episerver/opticli", root.GetProperty("issuer").GetString());
        Assert.Equal("http://localhost/episerver/opticli/oauth/authorize", root.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("http://localhost/episerver/opticli/oauth/token", root.GetProperty("token_endpoint").GetString());
        Assert.Equal("http://localhost/episerver/opticli/oauth/register", root.GetProperty("registration_endpoint").GetString());
        Assert.Equal(new[] { "S256" }, root.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains("none", root.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(s => s.GetString()));
        Assert.True(root.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.True(root.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/episerver/opticli/mcp")]
    [InlineData("/.well-known/oauth-authorization-server/episerver/opticli")]
    [InlineData("/.well-known/openid-configuration/episerver/opticli")]
    [InlineData("/episerver/opticli/.well-known/openid-configuration")]
    public async Task The_metadata_documents_name_the_requests_host_so_no_cache_keeps_them(string path)
    {
        // A copy made for a request with a forged Host header would send everyone else there.
        var response = await _site.Client("evil.example").GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("evil.example", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/openid-configuration")]
    public async Task The_sites_root_documents_are_left_alone(string path) =>
        Assert.Equal(HttpStatusCode.NotFound, (await _site.Client().GetAsync(path)).StatusCode);

    [Fact]
    public async Task The_whoami_tool_runs_as_the_editor_through_the_sdk_client()
    {
        await using var client = await Connect("editor");
        var tools = await client.ListToolsAsync();
        var whoami = Assert.Single(tools, t => t.Name == "whoami");
        Assert.True(whoami.ProtocolTool.Annotations?.ReadOnlyHint);

        // The package's own version: Directory.Build.props' version with the package's -preview suffix.
        Assert.Equal(("opticli", typeof(OptiCliMcpOptions).Assembly.GetName().Version!.ToString(3) + "-preview"), (client.ServerInfo.Name, client.ServerInfo.Version));
        Assert.Equal(client.ServerInfo.Version, OptiCliMcpExtensions.Version);

        var result = await client.CallToolAsync("whoami", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
        using var body = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        Assert.Equal("editor", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("MCP test client", body.RootElement.GetProperty("client").GetString());
        Assert.Equal(new[] { "content:read", "content:write" }, body.RootElement.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.False(body.RootElement.GetProperty("site").GetProperty("allowPublish").GetBoolean());
        Assert.Contains(_site.Audit.Audit, m => m.Contains("MCP tool whoami ok") && m.Contains("user editor"));
    }

    [Fact]
    public async Task Revoking_the_grant_stops_the_token_at_once()
    {
        var tokens = await _site.ConnectAsync();
        Assert.Equal(HttpStatusCode.OK, (await Call(tokens.Access, Initialize())).StatusCode);
        var grant = Assert.Single(await _site.Store.ListGrantsAsync(null, default));

        var browser = _site.Browser("editor");
        var page = await (await browser.GetAsync("/episerver/opticli/connections")).Content.ReadAsStringAsync();
        var form = Browser.Inputs(page);
        Assert.Equal(grant.GrantId, form["grant"]);
        var revoked = await browser.PostFormAsync("/episerver/opticli/connections", form);
        Assert.Equal(HttpStatusCode.SeeOther, revoked.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(tokens.Access, Initialize())).StatusCode);
        Assert.Equal("invalid_grant", await AuthorizationServerTests.Error(await _site.TokenAsync(AuthorizationServerTests.RefreshForm(tokens))));
    }

    [Fact]
    public async Task Adding_the_module_keeps_a_single_scheme_sites_implicit_default()
    {
        // A site that never named a default: with only its own scheme, ASP.NET Core used that one implicitly.
        await using var site = await TestSite.StartAsync(services: s => s.Configure<AuthenticationOptions>(o => o.DefaultScheme = null));
        var schemes = site.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(TestLogin.Scheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal(TestLogin.Scheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
    }

    [Fact]
    public async Task The_module_never_becomes_the_default_scheme()
    {
        var schemes = _site.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(TestLogin.Scheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
    }

    /// <summary>The official SDK client doing what Claude does: discovery from the 401, registration, sign-in and consent, token.</summary>
    private async Task<McpClient> Connect(string user)
    {
        var browser = _site.Browser(user);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/episerver/opticli/mcp"),
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri(TestSite.RedirectUri),
                DynamicClientRegistration = new() { ClientName = "MCP test client" },
                AuthorizationCallbackHandler = async (context, cancellationToken) =>
                {
                    var page = await browser.GetAsync(context.AuthorizationUri.PathAndQuery);
                    var html = await page.Content.ReadAsStringAsync(cancellationToken);
                    Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                    var posted = await browser.PostFormAsync(Browser.FormAction(html), Browser.Inputs(html, "allow").Concat(Browser.Ticked(html)));
                    var back = QueryHelpers.ParseQuery(posted.Headers.Location!.Query);
                    return new AuthorizationResult { Code = back["code"], State = back["state"], Iss = back["iss"] };
                },
            },
        }, _site.Client(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private async Task<HttpResponseMessage> Call(string token, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/episerver/opticli/mcp") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await _site.Client().SendAsync(request);
    }

    private static StringContent Initialize() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
        Encoding.UTF8,
        "application/json");

    private async Task<JsonElement> Json(string path)
    {
        var response = await _site.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
