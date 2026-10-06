using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests.OAuth;

public class ClientMetadataDocumentTests
{
    private const string Id = "https://client.example/oauth/metadata.json";

    private static (RegisteredClient? Client, string? Error) Parse(string json, string id = Id) =>
        ClientMetadataDocument.Parse(id, Encoding.UTF8.GetBytes(json), DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_public_client_naming_itself_is_accepted()
    {
        var (client, error) = Parse($$"""
            {"client_id":"{{Id}}","client_name":"Example","redirect_uris":["http://127.0.0.1/callback","https://client.example/cb"],
             "grant_types":["authorization_code","refresh_token"],"response_types":["code"],"token_endpoint_auth_method":"none"}
            """);
        Assert.Null(error);
        Assert.Equal("Example", client!.ClientName);
        Assert.Equal(ClientAuthMethods.None, client.AuthMethod);
        Assert.Equal(2, client.RedirectUris.Count);
    }

    [Fact]
    public void A_document_may_list_return_addresses_on_other_hosts_too_authorize_decides()
    {
        var (client, error) = Parse($$"""{"client_id":"{{Id}}","redirect_uris":["https://claude.ai/api/mcp/auth_callback","https://client.example/cb"]}""");
        Assert.Null(error);
        Assert.Equal(2, client!.RedirectUris.Count);
    }

    [Fact]
    public void Without_a_name_the_host_is_shown() =>
        Assert.Equal("client.example", Parse($$"""{"client_id":"{{Id}}","redirect_uris":["https://client.example/cb"]}""").Client!.ClientName);

    [Theory]
    [InlineData("""{"client_id":"https://other.example/oauth/metadata.json","redirect_uris":["https://client.example/cb"]}""", "client_id")]
    [InlineData("""{"redirect_uris":["https://client.example/cb"]}""", "client_id")]
    [InlineData("""{"client_id":"ID","redirect_uris":["https://client.example/cb"],"token_endpoint_auth_method":"client_secret_basic"}""", "public client")]
    [InlineData("""{"client_id":"ID","redirect_uris":["https://client.example/cb"],"client_secret":"s"}""", "secret")]
    [InlineData("""{"client_id":"ID","redirect_uris":[]}""", "redirect_uris")]
    [InlineData("""{"client_id":"ID","redirect_uris":["http://client.example/cb"]}""", "redirect_uris")]
    [InlineData("""{"client_id":"ID","redirect_uris":["https://client.example/cb#x"]}""", "redirect_uris")]
    [InlineData("""{"client_id":"ID","redirect_uris":"https://client.example/cb"}""", "redirect_uris")]
    [InlineData("""{"client_id":"ID","redirect_uris":["https://client.example/cb"],"grant_types":["client_credentials"]}""", "authorization code")]
    [InlineData("""{"client_id":"ID","redirect_uris":["https://client.example/cb"],"response_types":["token"]}""", "authorization code")]
    [InlineData("""["client_id"]""", "object")]
    [InlineData("""not json""", "JSON")]
    public void Documents_that_break_the_rules_are_refused(string json, string reason)
    {
        var (client, error) = Parse(json.Replace("\"ID\"", $"\"{Id}\""));
        Assert.Null(client);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void A_document_over_5_kb_is_refused()
    {
        var padding = new string('x', ClientMetadataDocument.MaxBytes);
        Assert.Contains("larger", Parse($$"""{"client_id":"{{Id}}","redirect_uris":["https://client.example/cb"],"client_uri":"{{padding}}"}""").Error);
    }

    [Fact]
    public void A_long_name_is_cut_and_control_characters_are_removed()
    {
        var name = Parse($$"""{"client_id":"{{Id}}","client_name":"Evil\u0000\nApp{{new string('a', 200)}}","redirect_uris":["https://client.example/cb"]}""").Client!.ClientName;
        Assert.StartsWith("EvilApp", name);
        Assert.Equal(ClientMetadataDocument.MaxNameLength, name.Length);
    }

    [Theory]
    [InlineData("https://client.example/oauth/metadata.json", false, true)]
    [InlineData("https://client.example:8443/metadata", false, true)]
    [InlineData("http://client.example/metadata", false, false)]
    [InlineData("http://client.example/metadata", true, false)]
    [InlineData("http://127.0.0.1:5000/metadata", false, false)]
    [InlineData("http://127.0.0.1:5000/metadata", true, true)]
    [InlineData("https://client.example", false, false)]
    [InlineData("https://client.example/", false, false)]
    [InlineData("https://client.example/a/../metadata", false, false)]
    [InlineData("https://client.example/./metadata", false, false)]
    [InlineData("https://client.example/metadata#x", false, false)]
    [InlineData("https://user@client.example/metadata", false, false)]
    public void Client_id_urls_follow_the_draft(string url, bool development, bool allowed) =>
        Assert.Equal(allowed, ClientMetadataDocument.CheckUrl(url, development, out _) is null);
}

/// <summary>The fetch itself, against a real loopback server: what the socket check lets through and what it refuses.</summary>
public sealed class ClientResolverTests : IAsyncLifetime
{
    private WebApplication _server = null!;
    private string _origin = "";
    private readonly ManualTime _time = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.MapGet("/client.json", (HttpContext c) => Results.Text(Document(c, "Loopback test client"), "application/json"));
        _server.MapGet("/big.json", (HttpContext c) => Results.Text(Document(c, new string('x', 6000)) , "application/json"));
        _server.MapGet("/redirect.json", () => Results.Redirect("/client.json"));
        _server.MapGet("/elsewhere.json", (HttpContext c) => Results.Text(
            $$"""{"client_id":"{{c.Request.Scheme}}://{{c.Request.Host}}{{c.Request.Path}}","client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback","https://attacker.example/cb"]}""",
            "application/json"));
        _server.MapGet("/slow.json", async (HttpContext c) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), c.RequestAborted);
            return Results.Text(Document(c, "slow"), "application/json");
        });
        await _server.StartAsync();
        _origin = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    private static string Document(HttpContext context, string name) =>
        $$"""{"client_id":"{{context.Request.Scheme}}://{{context.Request.Host}}{{context.Request.Path}}","client_name":"{{name}}","redirect_uris":["http://127.0.0.1/callback"]}""";

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private ClientResolver Resolver(string environment) =>
        new(new InMemoryOAuthStore(), new Environment(environment), NullLogger<ClientResolver>.Instance, _time);

    [Fact]
    public async Task A_loopback_document_is_fetched_in_development()
    {
        using var resolver = Resolver(Environments.Development);
        var client = await resolver.ResolveAsync(_origin + "/client.json", default);
        Assert.Equal("Loopback test client", client?.ClientName);
    }

    [Fact]
    public async Task A_loopback_document_is_refused_outside_development()
    {
        using var resolver = Resolver(Environments.Production);
        Assert.Null(await resolver.ResolveAsync(_origin + "/client.json", default));
    }

    [Fact]
    public async Task The_socket_check_refuses_loopback_even_when_the_url_passed()
    {
        // As if a public name had been rebound to 127.0.0.1 after it was checked: the connection itself is refused.
        using var http = new HttpClient(ClientResolver.CreateHandler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(_origin + "/client.json"));
        Assert.IsType<ClientResolver.RefusedAddressException>(error.InnerException);
    }

    [Theory]
    [InlineData("http://10.0.0.1/client.json")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[fd00::1]/client.json")]
    public async Task Private_addresses_are_refused_before_connecting(string url)
    {
        using var http = new HttpClient(ClientResolver.CreateHandler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
        Assert.IsType<ClientResolver.RefusedAddressException>(error.InnerException);
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        using var resolver = Resolver(Environments.Development);
        Assert.Null(await resolver.ResolveAsync(_origin + "/redirect.json", default));
    }

    [Fact]
    public async Task A_document_over_5_kb_is_refused()
    {
        using var resolver = Resolver(Environments.Development);
        Assert.Null(await resolver.ResolveAsync(_origin + "/big.json", default));
    }

    [Fact]
    public async Task A_slow_document_times_out_after_5_seconds()
    {
        using var resolver = Resolver(Environments.Development);
        var started = DateTime.UtcNow;
        Assert.Null(await resolver.ResolveAsync(_origin + "/slow.json", default));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_document_is_cached_for_10_minutes()
    {
        using var resolver = Resolver(Environments.Development);
        var url = _origin + "/client.json";
        var first = await resolver.ResolveAsync(url, default);
        await _server.StopAsync();
        Assert.Same(first, await resolver.ResolveAsync(url, default));
        _time.Advance(ClientResolver.CacheFor + TimeSpan.FromSeconds(1));
        Assert.Null(await resolver.ResolveAsync(url, default));
    }

    [Fact]
    public async Task A_metadata_document_client_signs_in_and_gets_tokens_without_registering()
    {
        await using var site = await TestSite.StartAsync(environment: Environments.Development);
        var clientId = _origin + "/client.json";
        var verifier = TestSite.Verifier();
        var browser = site.Browser("editor");
        var page = await browser.GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(verifier), "http://127.0.0.1:50000/callback"));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Connect Loopback test client?", html);
        Assert.Contains("published by <b>127.0.0.1</b>", html);

        var posted = await browser.PostFormAsync(Browser.FormAction(html), Browser.Inputs(html, "allow"));
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(posted.Headers.Location!.Query)["code"].ToString();
        var response = await site.TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "http://127.0.0.1:50000/callback",
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var grant = Assert.Single(await site.Store.ListGrantsAsync(null, default));
        Assert.Equal("Loopback test client", grant.ClientName);
        Assert.Null(await site.Store.FindClientAsync(clientId, default));
    }

    [Fact]
    public async Task A_metadata_document_clients_return_address_on_another_host_is_refused_at_authorize()
    {
        await using var site = await TestSite.StartAsync(environment: Environments.Development);
        var clientId = _origin + "/elsewhere.json";
        var browser = site.Browser("editor");

        var elsewhere = await browser.GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()), "https://attacker.example/cb"));
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Null(elsewhere.Headers.Location);
        Assert.Contains("Return address not allowed", await elsewhere.Content.ReadAsStringAsync());

        var claude = await browser.GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()), "https://claude.ai/api/mcp/auth_callback"));
        Assert.Equal(HttpStatusCode.OK, claude.StatusCode);
        // Published by a domain: no warning that it named itself.
        Assert.DoesNotContain("class=\"warning\"", await claude.Content.ReadAsStringAsync());
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
