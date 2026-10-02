using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OptiCli.Mcp.Integration.Support;

/// <summary>A tool's result: its JSON text, and whether it is an error result (the agent's error JSON then).</summary>
public sealed record ToolResult(bool IsError, string Text)
{
    public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();
}

/// <summary>How a session signs in.</summary>
internal sealed record SessionOptions
{
    /// <summary>
    /// A client ID metadata document's URL to identify with instead of registering (DCR). Given to the SDK as a
    /// pre-registered client id: its own CIMD support only takes https documents, and the test site's is http on loopback,
    /// which the module accepts in Development. The server's side, fetching and checking the document, is the same.
    /// </summary>
    public Uri? ClientMetadataDocument { get; init; }

    /// <summary>The site; the default one when null.</summary>
    public Uri? Site { get; init; }

    /// <summary>What the editor presses on the consent page: allow or deny.</summary>
    public string Decision { get; init; } = "allow";
}

/// <summary>
/// An MCP client as Claude is one: the official SDK's, which finds the authorization server from the endpoint's 401,
/// registers itself (or uses a metadata document), and signs in with the authorization code flow and PKCE. The
/// browser part, the CMS login and the consent page, is scripted by <see cref="Browser"/>.
/// </summary>
internal sealed class McpSession : IAsyncDisposable
{
    public const string McpPath = "episerver/opticli/mcp";

    /// <summary>Any loopback port works (RFC 8252); nothing listens there: the browser stops at the redirect.</summary>
    public static readonly Uri RedirectUri = new("http://127.0.0.1:53682/callback");

    private McpSession(Uri site, string user, Browser browser, RecordingTokenCache tokens, Traffic traffic, HttpClient http)
    {
        Site = site;
        User = user;
        Browser = browser;
        Tokens = tokens;
        Traffic = traffic;
        Http = http;
    }

    public Uri Site { get; }

    public string User { get; }

    public Browser Browser { get; }

    /// <summary>The client's tokens, which a test may read or tamper with.</summary>
    public RecordingTokenCache Tokens { get; }

    /// <summary>Every request the client made, for checking what it did (registered, refreshed).</summary>
    public Traffic Traffic { get; }

    /// <summary>A plain HTTP client for the site, for requests the SDK wouldn't make (a refresh token used twice).</summary>
    public HttpClient Http { get; }

    public McpClient Client { get; private set; } = null!;

    /// <summary>When false, a new sign-in (after a refused refresh, say) fails at once instead of signing in again.</summary>
    public bool AllowSignIn { get; set; } = true;

    public Uri Endpoint => new(Site, McpPath);

    /// <summary>Signs in as <paramref name="user"/> and connects.</summary>
    /// <remarks>
    /// The module limits OAuth requests per address and minute (SharedSessions). Should a run right after another one
    /// go over, the sign-in waits for the next window rather than failing: the limit is the module working, not a fault.
    /// </remarks>
    public static async Task<McpSession> ConnectAsync(string user, SessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ConnectOnceAsync(user, options, cancellationToken);
            }
            catch (Exception e) when (attempt < 3 && Messages(e).Contains("slow_down", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
    }

    private static string Messages(Exception e) => e.InnerException is null ? e.Message : $"{e.Message} {Messages(e.InnerException)}";

    private static async Task<McpSession> ConnectOnceAsync(string user, SessionOptions? options, CancellationToken cancellationToken)
    {
        var site = options?.Site ?? McpSiteSettings.Url!;
        var browser = new Browser(site, user, McpSiteSettings.Password(user)) { Decision = options?.Decision ?? "allow" };
        var traffic = new Traffic();
        var http = new HttpClient(new Traffic.Handler(traffic, new HttpClientHandler { AllowAutoRedirect = false })) { BaseAddress = site };
        var session = new McpSession(site, user, browser, new RecordingTokenCache(), traffic, http);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = session.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = RedirectUri,
                DynamicClientRegistration = new() { ClientName = $"opticli E2E tests ({user})" },
                ClientId = options?.ClientMetadataDocument?.ToString(),
                TokenCache = session.Tokens,
                AuthorizationCallbackHandler = session.AuthorizeAsync,
            },
        }, http, ownsHttpClient: false);
        try
        {
            session.Client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        return session;
    }

    private async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        if (!AllowSignIn)
        {
            throw new SignInBlockedException();
        }
        var back = await Browser.AuthorizeAsync(context.AuthorizationUri, context.RedirectUri, cancellationToken);
        var query = HttpUtility.ParseQueryString(back.Query);
        if (query["error"] is { } error)
        {
            throw new InvalidOperationException($"Authorization refused: {error} {query["error_description"]}");
        }
        return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
    }

    public async Task<ToolResult> CallAsync(string tool, object? arguments = null, CancellationToken cancellationToken = default)
    {
        var values = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments, Json))!.ToDictionary(p => p.Key, p => (object?)p.Value);
        var result = await Client.CallToolAsync(tool, values, cancellationToken: cancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return new ToolResult(result.IsError == true, text);
    }

    /// <summary>A tool call that must succeed: its JSON.</summary>
    public async Task<JsonElement> OkAsync(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.False(result.IsError, $"{tool} failed: {result.Text}");
        return result.Json;
    }

    /// <summary>A tool call that must fail: the error JSON (<c>code</c>, <c>message</c>, <c>hint</c>, <c>reason</c>).</summary>
    public async Task<JsonElement> ErrorAsync(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.True(result.IsError, $"{tool} should have failed: {result.Text}");
        return result.Json;
    }

    /// <summary>A JSON-RPC request to the MCP endpoint with this token, outside the SDK.</summary>
    public async Task<HttpResponseMessage> PostMcpAsync(string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpPath)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return await Http.SendAsync(request);
    }

    /// <summary>A refresh at the token endpoint, as the client's registration says to authenticate.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> RefreshAsync(string refreshToken)
    {
        var tokens = Tokens.Current ?? throw new InvalidOperationException("No tokens yet.");
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken };
        var request = new HttpRequestMessage(HttpMethod.Post, "episerver/opticli/oauth/token");
        switch (tokens.TokenEndpointAuthMethod)
        {
            case "client_secret_basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(tokens.ClientId!)}:{Uri.EscapeDataString(tokens.ClientSecret!)}")));
                break;
            case "client_secret_post":
                form["client_id"] = tokens.ClientId!;
                form["client_secret"] = tokens.ClientSecret!;
                break;
            default:
                form["client_id"] = tokens.ClientId!;
                break;
        }
        request.Content = new FormUrlEncodedContent(form);
        using var response = await Http.SendAsync(request);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    /// <summary>Revokes this client's connections on the connections page, so a test leaves no grant behind.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client is not null)
            {
                await Client.DisposeAsync();
            }
            if (Tokens.Current?.ClientId is { } clientId)
            {
                await Browser.RevokeAsync(clientId, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is HttpRequestException or PageException or InvalidOperationException)
        {
            // Best effort: a revoked or refused connection has nothing left to clean up.
        }
        Browser.Dispose();
        Http.Dispose();
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Thrown by the authorization callback when the test said a new sign-in isn't expected.</summary>
public sealed class SignInBlockedException() : Exception("The client tried to sign in again.");

/// <summary>The SDK's token cache, kept where the test can see and change it.</summary>
internal sealed class RecordingTokenCache : ITokenCache
{
    private readonly object _lock = new();
    private TokenContainer? _current;

    public TokenContainer? Current
    {
        get { lock (_lock) { return _current; } }
    }

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _current = tokens;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Current);

    /// <summary>Makes the client use <paramref name="accessToken"/> next, as if the one it has had expired or been replaced.</summary>
    public void ReplaceAccessToken(string accessToken)
    {
        lock (_lock)
        {
            _current = Copy(_current!, accessToken);
        }
    }

    private static TokenContainer Copy(TokenContainer tokens, string accessToken) => new()
    {
        TokenType = tokens.TokenType,
        AccessToken = accessToken,
        RefreshToken = tokens.RefreshToken,
        ExpiresIn = tokens.ExpiresIn,
        Scope = tokens.Scope,
        ObtainedAt = tokens.ObtainedAt,
        ClientId = tokens.ClientId,
        ClientSecret = tokens.ClientSecret,
        TokenEndpointAuthMethod = tokens.TokenEndpointAuthMethod,
        AuthorizationServer = tokens.AuthorizationServer,
    };
}

/// <summary>The requests an HTTP client made: method, path, status and, for the token endpoint, the grant type.</summary>
internal sealed class Traffic
{
    private readonly ConcurrentQueue<(string Method, string Path, HttpStatusCode Status, string? Grant)> _requests = new();

    public IReadOnlyList<(string Method, string Path, HttpStatusCode Status, string? Grant)> Requests => [.. _requests];

    /// <summary>Token requests for this grant type, with their status.</summary>
    public IReadOnlyList<HttpStatusCode> TokenRequests(string grantType) =>
        [.. _requests.Where(r => r.Path.EndsWith("/oauth/token", StringComparison.Ordinal) && r.Grant == grantType).Select(r => r.Status)];

    internal sealed class Handler(Traffic traffic, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? grant = null;
            if (request.Content is FormUrlEncodedContent form)
            {
                grant = HttpUtility.ParseQueryString(await form.ReadAsStringAsync(cancellationToken))["grant_type"];
            }
            var response = await base.SendAsync(request, cancellationToken);
            traffic._requests.Enqueue((request.Method.Method, request.RequestUri!.AbsolutePath, response.StatusCode, grant));
            return response;
        }
    }
}
