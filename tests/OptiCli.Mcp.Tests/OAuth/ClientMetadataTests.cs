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
    // Bidi controls: the name would read "Claude" followed by another app's name, reversed.
    [InlineData("Claude\u202Eppa.live\u202C", "Claudeppa.live")]
    [InlineData("\u2066Claude\u2069", "Claude")]
    // Zero-width spaces, joiners, the byte order mark, a soft hyphen, line and paragraph separators.
    [InlineData("Cl\u200Ba\u200Cu\u200Dd\uFEFFe\u00AD", "Claude")]
    [InlineData("Claude\u2028Desktop\u2029", "ClaudeDesktop")]
    // A tag character, outside the BMP (Cf as well).
    [InlineData("Claude\U000E0041", "Claude")]
    // What a name may have: letters of any script, accents, emoji.
    [InlineData("Ærlig Klient ✓ 😀", "Ærlig Klient ✓ 😀")]
    [InlineData("\u202E\u200B", "fallback")]
    public void Invisible_and_direction_changing_characters_are_removed_from_names(string name, string shown) =>
        Assert.Equal(shown, ClientMetadataDocument.CleanName(name, "fallback"));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "claude.ai")]
    [InlineData("https://сlaude.ai/cb", "xn--laude-0ye.ai")]
    [InlineData("http://127.0.0.1:53682/callback", "127.0.0.1:53682")]
    [InlineData("http://[::1]:53682/callback", "[::1]:53682")]
    public void A_return_address_is_shown_with_its_host_in_punycode(string url, string shown) =>
        Assert.Equal(shown, AuthorizationServer.IdnAuthority(new Uri(url)));

    [Fact]
    public void A_lone_surrogate_is_removed_from_a_name() =>
        Assert.Equal("Claude", ClientMetadataDocument.CleanName("Claude\uD800", "fallback"));

    [Fact]
    public void A_name_is_not_cut_inside_a_character()
    {
        var name = ClientMetadataDocument.CleanName(new string('a', ClientMetadataDocument.MaxNameLength - 1) + "😀b", "fallback");
        Assert.Equal(new string('a', ClientMetadataDocument.MaxNameLength - 1), name);
    }

    [Fact]
    public void Without_a_name_a_lookalike_host_is_shown_in_punycode()
    {
        // Cyrillic es (U+0441) in place of the Latin "c".
        const string id = "https://\u0441laude.ai/oauth/metadata.json";
        var client = Parse($$"""{"client_id":"{{id}}","redirect_uris":["https://claude.ai/api/mcp/auth_callback"]}""", id).Client!;
        Assert.StartsWith("xn--", client.ClientName);
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
    private int _counted;

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
        _server.MapGet("/counted.json", (HttpContext c) =>
        {
            Interlocked.Increment(ref _counted);
            return Results.Text(Document(c, "Counted client"), "application/json");
        });
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
    public async Task A_token_request_with_a_made_up_code_or_refresh_token_fetches_no_document()
    {
        await using var site = await TestSite.StartAsync(environment: Environments.Development);
        var clientId = _origin + "/counted.json";

        var refresh = await site.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "ocr_madeup", ["client_id"] = clientId });
        Assert.Equal("invalid_grant", await AuthorizationServerTests.Error(refresh));
        var code = await site.TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = "madeup", ["redirect_uri"] = "http://127.0.0.1/callback", ["client_id"] = clientId,
            ["code_verifier"] = TestSite.Verifier(),
        });
        Assert.Equal("invalid_grant", await AuthorizationServerTests.Error(code));
        // A real refresh token of another client doesn't get it fetched either.
        var other = await site.ConnectAsync();
        var theirs = await site.TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = other.Refresh, ["client_id"] = clientId });
        Assert.Equal("invalid_grant", await AuthorizationServerTests.Error(theirs));

        Assert.Equal(0, _counted);
        // The other client's connection is untouched.
        Assert.Single(await site.Store.ListGrantsAsync(null, default));
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

    internal sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>How many documents the resolver fetches at once, and which it keeps: with a stand-in for the network.</summary>
public sealed class ClientResolverLimitsTests
{
    private readonly ManualTime _time = new();

    private ClientResolver Resolver(HttpMessageHandler handler) =>
        new(new InMemoryOAuthStore(), new ClientResolverTests.Environment(Environments.Production), NullLogger<ClientResolver>.Instance, _time, handler);

    private static string ClientId(int i) => $"https://client{i}.example.com/client.json";

    [Fact]
    public async Task At_most_four_documents_are_fetched_at_once()
    {
        var network = new Network { Hold = new TaskCompletionSource() };
        using var resolver = Resolver(network);
        var resolving = Enumerable.Range(0, 10).Select(i => resolver.ResolveAsync(ClientId(i), default)).ToList();
        while (network.Fetches < ClientResolver.MaxConcurrentFetches)
        {
            await Task.Delay(10);
        }
        await Task.Delay(200);
        Assert.Equal(ClientResolver.MaxConcurrentFetches, network.Fetches);

        network.Hold.SetResult();
        var clients = await Task.WhenAll(resolving);
        Assert.All(clients, Assert.NotNull);
        Assert.Equal(10, network.Fetches);
        Assert.Equal(ClientResolver.MaxConcurrentFetches, network.MostAtOnce);
    }

    [Fact]
    public async Task A_full_cache_drops_the_least_recently_used_document()
    {
        var network = new Network();
        using var resolver = Resolver(network);
        for (var i = 0; i < ClientResolver.MaxCached; i++)
        {
            await resolver.ResolveAsync(ClientId(i), default);
        }
        // Used again, so client1 is now the least recently used.
        await resolver.ResolveAsync(ClientId(0), default);
        await resolver.ResolveAsync(ClientId(ClientResolver.MaxCached), default);
        var fetched = network.Fetches;

        await resolver.ResolveAsync(ClientId(0), default);
        await resolver.ResolveAsync(ClientId(2), default);
        Assert.Equal(fetched, network.Fetches);
        await resolver.ResolveAsync(ClientId(1), default);
        Assert.Equal(fetched + 1, network.Fetches);
    }

    [Fact]
    public async Task The_consent_page_shows_a_lookalike_domain_in_punycode_and_a_caution()
    {
        // Cyrillic es (U+0441) in place of the Latin "c", and a name that turns its end around.
        const string clientId = "https://\u0441laude.ai/oauth/client.json";
        var document = $$"""{"client_id":"{{clientId}}","client_name":"Claude\u202Eai.edualc\u202C","redirect_uris":["https://claude.ai/api/mcp/auth_callback"]}""";
        await using var site = await TestSite.StartAsync(after: s => s.AddSingleton(sp => new ClientResolver(sp.GetRequiredService<IOAuthStore>(),
            sp.GetRequiredService<IHostEnvironment>(), NullLogger<ClientResolver>.Instance, sp.GetRequiredService<TimeProvider>(), new Fixed(document))));

        var page = await site.Browser("editor").GetAsync(TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()), "https://claude.ai/api/mcp/auth_callback"));
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains($"published by <b>{new Uri(clientId).IdnHost}</b>", html);
        Assert.StartsWith("xn--", new Uri(clientId).IdnHost);
        Assert.DoesNotContain("\u0441laude", html, StringComparison.Ordinal);
        // Ordinal: a culture-aware search ignores the bidi control, and finds it anywhere.
        Assert.DoesNotContain("\u202E", html, StringComparison.Ordinal);
        Assert.Contains("Connect Claudeai.edualc?", html);
        // Every app gets the caution; only one that registered itself gets the warning.
        Assert.Contains("Only allow it if you just added this connection yourself.", html);
        Assert.DoesNotContain("class=\"warning\"", html);
        Assert.Contains("sent back to <b>claude.ai</b>", html);
    }

    /// <summary>Answers every request with the same document.</summary>
    private sealed class Fixed(string document) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document, Encoding.UTF8, "application/json") });
    }

    /// <summary>Answers every request with a valid document for its URL, once <see cref="Hold"/> (if any) is done.</summary>
    private sealed class Network : HttpMessageHandler
    {
        private int _fetches;
        private int _now;
        private int _most;

        public TaskCompletionSource? Hold { get; init; }

        public int Fetches => Volatile.Read(ref _fetches);

        public int MostAtOnce => Volatile.Read(ref _most);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetches);
            var now = Interlocked.Increment(ref _now);
            int most;
            while ((most = Volatile.Read(ref _most)) < now && Interlocked.CompareExchange(ref _most, now, most) != most)
            {
            }
            try
            {
                if (Hold is not null)
                {
                    await Hold.Task.WaitAsync(cancellationToken);
                }
                var document = $$"""{"client_id":"{{request.RequestUri}}","client_name":"Client","redirect_uris":["https://claude.ai/api/mcp/auth_callback"]}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document, Encoding.UTF8, "application/json") };
            }
            finally
            {
                Interlocked.Decrement(ref _now);
            }
        }
    }
}
