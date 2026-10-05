using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tests.Support;

/// <summary>
/// A site with the module on an in-memory test server: an in-memory store, a fake role store, a manual clock, and a
/// test login standing in for the site's own (a request is signed in as whoever its <c>X-Test-User</c> header names).
/// </summary>
internal sealed class TestSite : IAsyncDisposable
{
    public const string RedirectUri = "http://127.0.0.1:53682/callback";

    public InMemoryOAuthStore Store { get; } = new();
    public FakeEditorRoles Roles { get; } = new();
    public ManualTime Time { get; } = new();
    public AuditLog Audit { get; } = new();

    private WebApplication _app = null!;

    public TestServer Server => _app.GetTestServer();

    public IServiceProvider Services => _app.Services;

    public static async Task<TestSite> StartAsync(
        Action<OptiCliMcpOptions>? configure = null,
        Dictionary<string, string?>? configuration = null,
        OptiCliMcpRateLimits? limits = null,
        string environment = "Production",
        Action<IServiceCollection>? services = null,
        bool statusCodePages = false)
    {
        var site = new TestSite();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        // Limits high enough for any test, unless the test sets its own (limits, or the configuration).
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OptiCli:Mcp:RateLimits:RegisterPerMinute"] = "1000",
            ["OptiCli:Mcp:RateLimits:TokenPerMinute"] = "1000",
            ["OptiCli:Mcp:RateLimits:TokenPerAddressPerMinute"] = "1000",
            ["OptiCli:Mcp:RateLimits:AuthorizePerMinute"] = "1000",
        });
        builder.Configuration.AddInMemoryCollection(configuration ?? []);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(site.Audit);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication(o => o.DefaultScheme = TestLogin.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TestLogin>(TestLogin.Scheme, null);
        builder.Services.AddSingleton<IOAuthStore>(site.Store);
        builder.Services.AddSingleton<IEditorRoles>(site.Roles);
        builder.Services.AddSingleton<TimeProvider>(site.Time);
        services?.Invoke(builder.Services);
        builder.Services.AddOptiCliMcp(o =>
        {
            if (limits is not null)
            {
                o.RateLimits = limits;
            }
            configure?.Invoke(o);
        });

        var app = builder.Build();
        if (statusCodePages)
        {
            // As a site with custom error pages has it: an error without a body is re-executed, with the request's own
            // method, as the site's error page, which only answers GET.
            app.UseStatusCodePagesWithReExecute("/error/{0}");
            app.MapGet("/error/{code:int}", (int code) => Results.Text($"<h1>Site error page {code}</h1>", "text/html", statusCode: code));
        }
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOptiCliMcp();
        await app.StartAsync();
        site._app = app;
        return site;
    }

    public OptiCliMcpOptions Options => Services.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value;

    /// <summary>A client without cookies or a login: what an MCP client is.</summary>
    public HttpClient Client(string? host = null)
    {
        var client = Server.CreateClient();
        if (host is not null)
        {
            client.BaseAddress = new Uri($"http://{host}/");
        }
        return client;
    }

    /// <summary>A browser that keeps cookies, signed in as <paramref name="user"/> (nobody when null), not following redirects.</summary>
    public Browser Browser(string? user, string? cookieRoles = null) => new(Server, user, cookieRoles);

    /// <summary>Registers a client by DCR and returns its id (and secret for a confidential one).</summary>
    public async Task<(string ClientId, string? Secret)> RegisterAsync(string method = "none", string redirectUri = RedirectUri, string name = "Test client")
    {
        var response = await Client().PostAsync("/episerver/opticli/oauth/register", Json(new
        {
            redirect_uris = new[] { redirectUri },
            client_name = name,
            token_endpoint_auth_method = method,
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("client_id").GetString()!,
            body.RootElement.TryGetProperty("client_secret", out var secret) ? secret.GetString() : null);
    }

    public static StringContent Json(object value) => new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    public static string AuthorizeUrl(string clientId, string challenge, string redirectUri = RedirectUri, string state = "xyz", string? scope = null, string? resource = "http://localhost/episerver/opticli/mcp")
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        if (scope is not null)
        {
            query["scope"] = scope;
        }
        if (resource is not null)
        {
            query["resource"] = resource;
        }
        return QueryHelpers.AddQueryString("/episerver/opticli/oauth/authorize", query);
    }

    /// <summary>The whole sign-in as a browser does it: authorize page, consent, code.</summary>
    /// <param name="allow">The scopes the editor leaves ticked on the consent page; all that are offered when null.</param>
    /// <returns>The query of the redirect back to the client.</returns>
    public async Task<Dictionary<string, string>> AuthorizeAsync(string clientId, string verifier, string user = "editor", string decision = "allow", string? scope = null, string[]? allow = null)
    {
        var browser = Browser(user);
        var url = AuthorizeUrl(clientId, Pkce.Challenge(verifier), scope: scope);
        var page = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var ticked = Support.Browser.Ticked(html).Where(t => allow is null || allow.Contains(t.Value));
        var posted = await browser.PostFormAsync(Support.Browser.FormAction(html), Support.Browser.Inputs(html, decision).Concat(ticked));
        Assert.Equal(HttpStatusCode.SeeOther, posted.StatusCode);
        var location = posted.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, location, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(new Uri(location).Query).ToDictionary(p => p.Key, p => p.Value.ToString());
    }

    public async Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form, string? basic = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/episerver/opticli/oauth/token") { Content = new FormUrlEncodedContent(form) };
        if (basic is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        }
        return await Client().SendAsync(request);
    }

    /// <summary>Signs <paramref name="user"/> in for a new public client and exchanges the code.</summary>
    /// <param name="allow">The scopes the editor leaves ticked on the consent page; all that are offered when null.</param>
    public async Task<Tokens> ConnectAsync(string user = "editor", string? scope = null, string[]? allow = null)
    {
        var (clientId, _) = await RegisterAsync();
        var verifier = Verifier();
        var back = await AuthorizeAsync(clientId, verifier, user, scope: scope, allow: allow);
        var response = await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = back["code"],
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            ["resource"] = "http://localhost/episerver/opticli/mcp",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Tokens.ReadAsync(response, clientId);
    }

    public static string Verifier() => Secrets.New() + "-verifier";

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

internal sealed record Tokens(string ClientId, string Access, string Refresh, string Scope, int ExpiresIn)
{
    public static async Task<Tokens> ReadAsync(HttpResponseMessage response, string clientId)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        return new Tokens(clientId, root.GetProperty("access_token").GetString()!, root.GetProperty("refresh_token").GetString()!,
            root.GetProperty("scope").GetString()!, root.GetProperty("expires_in").GetInt32());
    }
}

/// <summary>The site's own login, faked: signed in as the <c>X-Test-User</c> header; a challenge goes to <c>/login</c>.</summary>
internal sealed class TestLogin(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "TestLogin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-User"].ToString() is not { Length: > 0 } user)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        // The roles "in the login cookie": what the module must not trust over the role store.
        var claims = new List<Claim> { new(ClaimTypes.Name, user) };
        claims.AddRange(Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/login?ReturnUrl=" + Uri.EscapeDataString(properties.RedirectUri ?? "/"));
        return Task.CompletedTask;
    }
}

/// <summary>Cookies and a signed-in user across requests, like a browser; redirects are left to the test.</summary>
internal sealed class Browser(TestServer server, string? user, string? cookieRoles)
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client = server.CreateClient();

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

    public Task<HttpResponseMessage> PostFormAsync(string url, Dictionary<string, string> form) => PostFormAsync(url, form.AsEnumerable());

    public Task<HttpResponseMessage> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) });

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        var uri = new Uri(_client.BaseAddress!, request.RequestUri!);
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }
        if (cookieRoles is not null)
        {
            request.Headers.Add("X-Test-Roles", cookieRoles);
        }
        if (_cookies.GetCookieHeader(uri) is { Length: > 0 } cookie)
        {
            request.Headers.Add("Cookie", cookie);
        }
        var response = await _client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                _cookies.SetCookies(uri, setCookie);
            }
        }
        return response;
    }

    /// <summary>
    /// The form's fields but its checkboxes, as a browser would post them when <paramref name="decision"/> is clicked;
    /// the ticked checkboxes are <see cref="Ticked"/>.
    /// </summary>
    public static Dictionary<string, string> Inputs(string html, string? decision = null)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(html, "<input\\b[^>]*>"))
        {
            string? Attr(string name) => Regex.Match(input.Value, $"\\b{name}=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
            if (Attr("name") is { } key && Attr("type") != "checkbox")
            {
                fields.TryAdd(key, Attr("value") ?? "");
            }
        }
        if (decision is not null)
        {
            fields["decision"] = decision;
        }
        return fields;
    }

    /// <summary>The checkboxes a browser would post: ticked, enabled and named.</summary>
    public static List<KeyValuePair<string, string>> Ticked(string html) =>
        Regex.Matches(html, "<input\\b[^>]*\\btype=\"checkbox\"[^>]*>")
            .Where(m => m.Value.Contains(" checked", StringComparison.Ordinal) && !m.Value.Contains(" disabled", StringComparison.Ordinal))
            .Select(m => (Name: Regex.Match(m.Value, "\\bname=\"([^\"]*)\""), Value: Regex.Match(m.Value, "\\bvalue=\"([^\"]*)\"")))
            .Where(m => m.Name.Success)
            .Select(m => KeyValuePair.Create(WebUtility.HtmlDecode(m.Name.Groups[1].Value), m.Value.Success ? WebUtility.HtmlDecode(m.Value.Groups[1].Value) : "on"))
            .ToList();

    public static string FormAction(string html) =>
        WebUtility.HtmlDecode(Regex.Match(html, "<form method=\"post\" action=\"([^\"]*)\"").Groups[1].Value);
}
