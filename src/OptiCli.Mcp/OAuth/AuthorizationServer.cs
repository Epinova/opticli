using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using static OptiCli.Mcp.OAuth.HtmlPage;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// A small OAuth 2.1 authorization server for MCP clients: dynamic client registration (RFC 7591), client ID metadata
/// documents, the authorization code flow with PKCE (S256 only), refresh tokens that rotate (with reuse detection,
/// RFC 9700), resource indicators (RFC 8707) and the <c>iss</c> response parameter (RFC 9207). Sign-in itself is the
/// site's own: <c>authorize</c> challenges the site's default scheme (ASP.NET Identity, Entra ID, Opti ID, ...), then
/// asks the editor to approve the client and choose its scopes.
/// </summary>
/// <remarks>
/// Not OpenIddict: CMS 12's own OpenID Connect package pins OpenIddict 3, which has neither DCR nor metadata documents,
/// and a second OpenIddict server can't run beside it.
/// </remarks>
internal sealed class AuthorizationServer(
    IOAuthStore store,
    ClientResolver clients,
    TokenService tokens,
    GrantCache cache,
    EditorGate gate,
    IAntiforgery antiforgery,
    IOptions<OptiCliMcpOptions> optionsAccessor,
    OAuthRateLimiter limiter,
    OAuthMaintenance maintenance,
    McpAudit audit,
    TimeProvider time)
{
    /// <summary>How long a code works: long enough to sign in, short enough that a leaked one is useless.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public const string RefreshPrefix = "ocr_";

    public const string SecretPrefix = "ocs_";

    private const int MaxRegistrationBytes = 16 * 1024;

    private const int MaxStateLength = 2000;

    private OptiCliMcpOptions Options => optionsAccessor.Value;

    // --- Metadata --------------------------------------------------------------------------------------------------

    /// <summary>Protected resource metadata (RFC 9728): where to get tokens for the MCP endpoint.</summary>
    public IResult ResourceMetadata(HttpContext context) => Results.Json(new Dictionary<string, object>
    {
        ["resource"] = McpUrls.Resource(context.Request, Options),
        ["authorization_servers"] = new[] { McpUrls.Issuer(context.Request, Options) },
        ["scopes_supported"] = Scopes.Supported(Options),
        ["bearer_methods_supported"] = new[] { "header" },
        ["resource_name"] = "Optimizely CMS content (opticli)",
    }, OAuthJson.Options);

    /// <summary>Authorization server metadata (RFC 8414), also served as OpenID Connect discovery for clients that only look there.</summary>
    public IResult ServerMetadata(HttpContext context)
    {
        var issuer = McpUrls.Issuer(context.Request, Options);
        var origin = McpUrls.Origin(context.Request);
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = origin + McpUrls.AuthorizePath(Options),
            ["token_endpoint"] = origin + McpUrls.TokenPath(Options),
            ["registration_endpoint"] = origin + McpUrls.RegisterPath(Options),
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = new[] { "query" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = ClientAuthMethods.All,
            ["scopes_supported"] = Scopes.Supported(Options),
            ["authorization_response_iss_parameter_supported"] = true,
            ["client_id_metadata_document_supported"] = true,
        }, OAuthJson.Options);
    }

    // --- Dynamic client registration ---------------------------------------------------------------------------------

    public async Task<IResult> Register(HttpContext context)
    {
        if (limiter.Check(OAuthRateLimiter.Register, context) is { } limited)
        {
            return limited;
        }
        var body = await ReadLimitedAsync(context.Request.Body, MaxRegistrationBytes, context.RequestAborted);
        if (body is null)
        {
            return OAuthJson.Error(context, "invalid_client_metadata", $"The registration is larger than {MaxRegistrationBytes} bytes.");
        }
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return OAuthJson.Error(context, "invalid_client_metadata", "The body isn't JSON.");
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            return OAuthJson.Error(context, "invalid_client_metadata", "The body must be a JSON object.");
        }

        if (!root.TryGetProperty("redirect_uris", out var urisElement) || urisElement.ValueKind != JsonValueKind.Array
            || urisElement.GetArrayLength() is 0 or > ClientMetadataDocument.MaxRedirectUris
            || urisElement.EnumerateArray().Any(u => u.ValueKind != JsonValueKind.String))
        {
            return OAuthJson.Error(context, "invalid_redirect_uri", $"redirect_uris must list 1 to {ClientMetadataDocument.MaxRedirectUris} URIs.");
        }
        var uris = urisElement.EnumerateArray().Select(u => u.GetString()!).ToList();
        if (uris.FirstOrDefault(u => !RedirectUris.IsAllowed(u)) is { } bad)
        {
            return OAuthJson.Error(context, "invalid_redirect_uri", $"{Shorten(bad)} must be https, or http on a loopback address, without a fragment.");
        }
        var method = String(root, "token_endpoint_auth_method") ?? ClientAuthMethods.SecretBasic;
        if (!ClientAuthMethods.All.Contains(method))
        {
            return OAuthJson.Error(context, "invalid_client_metadata", $"token_endpoint_auth_method must be one of {string.Join(", ", ClientAuthMethods.All)}.");
        }
        if (!Lists(root, "grant_types", ["authorization_code", "refresh_token"], "authorization_code")
            || !Lists(root, "response_types", ["code"], "code"))
        {
            return OAuthJson.Error(context, "invalid_client_metadata", "Only the authorization_code (and refresh_token) grant with response type code is supported.");
        }

        var secret = method == ClientAuthMethods.None ? null : SecretPrefix + Secrets.New();
        var now = time.GetUtcNow();
        var client = new RegisteredClient
        {
            ClientId = "mcp_" + Secrets.New()[..22],
            ClientName = ClientMetadataDocument.CleanName(String(root, "client_name"), "MCP client"),
            RedirectUris = uris,
            AuthMethod = method,
            SecretHash = secret is null ? "" : Secrets.Hash(secret),
            Created = now,
        };
        await store.AddClientAsync(client, context.RequestAborted);

        var response = new Dictionary<string, object>
        {
            ["client_id"] = client.ClientId,
            ["client_id_issued_at"] = now.ToUnixTimeSeconds(),
            ["client_name"] = client.ClientName,
            ["redirect_uris"] = uris,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = method,
        };
        if (secret is not null)
        {
            response["client_secret"] = secret;
            response["client_secret_expires_at"] = 0;
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(response, OAuthJson.Options, statusCode: StatusCodes.Status201Created);
    }

    // --- Authorization ---------------------------------------------------------------------------------------------

    private sealed record AuthorizeRequest(RegisteredClient Client, string RedirectUri, string State, string Challenge, string Scope, string Resource);

    /// <summary>
    /// Checks an authorization request the way RFC 6749 4.1.2.1 says: an unknown client or redirect URI gets an error
    /// page here, never a redirect (that would make the endpoint an open redirector); every other problem goes back to
    /// the client's (registered) redirect URI. Parameters always come from the query, for the consent post too.
    /// </summary>
    private async Task<(AuthorizeRequest? Request, IResult? Error)> ParseAsync(HttpContext context)
    {
        var query = context.Request.Query;
        // A parameter given twice is as good as missing (RFC 6749 3.1).
        string Get(string key) => query[key] is { Count: 1 } value ? value.ToString() : "";

        var client = await clients.ResolveAsync(Get("client_id"), context.RequestAborted);
        if (client is null)
        {
            return (null, Message(context, HttpStatusCode.BadRequest, "Unknown app",
                "This app isn't known to the site, or its description couldn't be loaded. Remove the connector in the app and add it again."));
        }
        var redirect = Get("redirect_uri");
        if (!RedirectUris.AnyMatches(client.RedirectUris, redirect))
        {
            return (null, Message(context, HttpStatusCode.BadRequest, "Wrong return address",
                "The app asked to be sent back somewhere it didn't register, so the site won't send it there."));
        }

        var state = Get("state");
        IResult Back(string error, string description) => Results.Redirect(Redirect(context, redirect, state, new() { ["error"] = error, ["error_description"] = description }));

        if (state.Length > MaxStateLength)
        {
            return (null, Back("invalid_request", "state is too long."));
        }
        if (Get("response_type") != "code")
        {
            return (null, Back("unsupported_response_type", "Only response_type=code is supported."));
        }
        if (Get("code_challenge_method") != "S256" || !Pkce.IsValidChallenge(Get("code_challenge")))
        {
            return (null, Back("invalid_request", "PKCE with code_challenge_method=S256 is required."));
        }
        var resource = McpUrls.Resource(context.Request, Options);
        var requestedResource = Get("resource");
        if (requestedResource.Length > 0 && !string.Equals(requestedResource.TrimEnd('/'), resource, StringComparison.Ordinal))
        {
            return (null, Back("invalid_target", $"This server only issues tokens for {resource}."));
        }
        var scope = Scopes.Grantable(Get("scope"), Options);
        if (scope.Length == 0)
        {
            return (null, Back("invalid_scope", $"Supported scopes: {string.Join(' ', Scopes.Supported(Options))}."));
        }
        return (new AuthorizeRequest(client, redirect, state, Get("code_challenge"), scope, resource), null);
    }

    public async Task<IResult> AuthorizePage(HttpContext context)
    {
        if (limiter.Check(OAuthRateLimiter.Authorize, context) is { } limited)
        {
            return limited;
        }
        var (request, error) = await ParseAsync(context);
        if (request is null)
        {
            return error!;
        }
        // Signed in with the site's own login? Otherwise there and back, with whatever scheme the site uses.
        if (SignedInName(context) is not { } user)
        {
            return Results.Challenge(new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path + context.Request.QueryString });
        }
        var (current, _) = await gate.CurrentAsync(context.User, context.RequestAborted);
        if (await gate.RefusalAsync(current, Options, context.RequestAborted) is { } refusal)
        {
            return Refused(context, user, request, refusal);
        }

        var form = antiforgery.GetAndStoreTokens(context);
        var redirect = new Uri(request.RedirectUri);
        var identity = ClientMetadataDocument.IsUrl(request.Client.ClientId)
            ? $"The app's description is published by <b>{H(new Uri(request.Client.ClientId).Host)}</b>."
            : "The app registered itself under this name; the site can't confirm who made it.";
        // Read is what every call needs, so it can't be unticked; the others are the editor's choice.
        var scopes = string.Concat(request.Scope.Split(' ').Select(s => s == Scopes.Read
            ? $"<li><label><input type=\"checkbox\" checked disabled> {H(Scopes.Describe(s))} (always)</label></li>"
            : $"<li><label><input type=\"checkbox\" name=\"scope\" value=\"{H(s)}\" checked> {H(Scopes.Describe(s))}</label></li>"));
        var noPublish = Options.AllowPublish ? "" : "<p class=\"muted\">This site doesn't let AI assistants publish: an editor publishes in the CMS.</p>";
        var action = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        return Render(context, HttpStatusCode.OK, "Connect an AI assistant", $"""
            <h1>Connect {H(request.Client.ClientName)}?</h1>
            <p>Signed in as <b>{H(user)}</b>. {identity}</p>
            <p>If you allow it, you'll be sent back to <b>{H(redirect.Authority)}</b>, and the app will act as you, with your access rights in the CMS. Untick what it shouldn't do. It may:</p>
            <form method="post" action="{H(action)}">
              <ul class="scopes">{scopes}</ul>
              {noPublish}
              <input type="hidden" name="{H(form.FormFieldName)}" value="{H(form.RequestToken)}">
              <button type="submit" name="decision" value="allow">Allow</button>
              <button type="submit" name="decision" value="deny">Deny</button>
            </form>
            <p class="muted">You can see and revoke your connections at <a href="{H(context.Request.PathBase + McpUrls.ConnectionsPath(Options))}">AI assistant connections</a>.</p>
            """, formTarget: redirect.GetLeftPart(UriPartial.Authority));
    }

    public async Task<IResult> AuthorizeConsent(HttpContext context)
    {
        if (limiter.Check(OAuthRateLimiter.Authorize, context) is { } limited)
        {
            return limited;
        }
        if (!context.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(context))
        {
            return Message(context, HttpStatusCode.BadRequest, "Page expired", "The page expired. Go back to the app and connect again.");
        }
        var (request, error) = await ParseAsync(context);
        if (request is null)
        {
            return error!;
        }
        if (SignedInName(context) is not { } user)
        {
            return Message(context, HttpStatusCode.Forbidden, "Not signed in", "Sign in to the site first, then connect again from the app.");
        }
        var (current, roles) = await gate.CurrentAsync(context.User, context.RequestAborted);
        if (await gate.RefusalAsync(current, Options, context.RequestAborted) is { } refusal)
        {
            return Refused(context, user, request, refusal);
        }
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["decision"] != "allow")
        {
            audit.Authorize(user, request.Client.ClientId, request.Client.ClientName, "denied", request.Scope);
            return SeeOther(context, Redirect(context, request.RedirectUri, request.State,
                new() { ["error"] = "access_denied", ["error_description"] = "The editor didn't allow the connection." }));
        }
        // What the editor left ticked, of what was offered: the code, and so the connection, gets only that.
        var scope = Scopes.Chosen(request.Scope, form["scope"]);

        var code = Secrets.New();
        await store.AddCodeAsync(new AuthorizationCode
        {
            CodeHash = Secrets.Hash(code),
            ClientId = request.Client.ClientId,
            ClientName = request.Client.ClientName,
            RedirectUri = request.RedirectUri,
            CodeChallenge = request.Challenge,
            UserName = user,
            Roles = roles,
            Scope = scope,
            Resource = request.Resource,
            Expires = time.GetUtcNow().Add(CodeLifetime),
        }, context.RequestAborted);
        audit.Authorize(user, request.Client.ClientId, request.Client.ClientName, "allowed", scope);
        return SeeOther(context, Redirect(context, request.RedirectUri, request.State, new() { ["code"] = code }));
    }

    /// <summary>The page for a user the gate turned away (<see cref="EditorGate.RefusalAsync"/>), audited.</summary>
    private IResult Refused(HttpContext context, string user, AuthorizeRequest request, GateRefusal refusal)
    {
        var (outcome, text) = refusal == GateRefusal.AccountDisabled
            ? ("refused (account disabled)", $"The account {user} is disabled on this site, so it can't connect an AI assistant.")
            : ("refused (not an editor)", $"{user} isn't an editor on this site, so it can't connect an AI assistant.");
        audit.Authorize(user, request.Client.ClientId, request.Client.ClientName, outcome, request.Scope);
        return Message(context, HttpStatusCode.Forbidden, "No access", text);
    }

    /// <summary>The signed-in user's name; null when nobody (with a name) is signed in.</summary>
    private static string? SignedInName(HttpContext context) =>
        context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;

    /// <summary>Back to the client's redirect URI, which was matched against its registered ones, with <c>state</c> and <c>iss</c> (RFC 9207).</summary>
    private string Redirect(HttpContext context, string redirectUri, string state, Dictionary<string, string?> parameters)
    {
        if (state.Length > 0)
        {
            parameters["state"] = state;
        }
        parameters["iss"] = McpUrls.Issuer(context.Request, Options);
        return QueryHelpers.AddQueryString(redirectUri, parameters);
    }

    /// <summary>303 after the consent post (OAuth 2.1 7.5.2): the browser follows with a GET, never re-posting the form.</summary>
    private static IResult SeeOther(HttpContext context, string location)
    {
        context.Response.Headers.Location = location;
        context.Response.Headers.CacheControl = "no-store";
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    // --- Token -----------------------------------------------------------------------------------------------------

    public async Task<IResult> Token(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (limiter.Check(OAuthRateLimiter.Token, context) is { } limited)
        {
            return limited;
        }
        if (!context.Request.HasFormContentType)
        {
            return OAuthJson.Error(context, "invalid_request", "Send the token request as application/x-www-form-urlencoded.");
        }
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form.Any(f => f.Value.Count > 1))
        {
            return OAuthJson.Error(context, "invalid_request", "A parameter was sent more than once.");
        }
        var grantType = form["grant_type"].ToString();
        var credentials = ClientCredentials.From(context.Request, form);
        if (credentials is null)
        {
            return OAuthJson.Error(context, "invalid_request", "The client id in the Authorization header and the form differ, or the header is malformed.");
        }
        // Per client as well as per address: claude.ai's token requests for every editor of a site come from the same
        // few Anthropic addresses.
        if (limiter.Check(OAuthRateLimiter.TokenClient, context, credentials.ClientId) is { } clientLimited)
        {
            return clientLimited;
        }
        var client = await clients.ResolveAsync(credentials.ClientId, context.RequestAborted);
        if (client is null || !credentials.Authenticates(client))
        {
            audit.TokenRefused(grantType, credentials.ClientId, "invalid_client");
            if (credentials.Method == ClientAuthMethods.SecretBasic)
            {
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"opticli\"";
            }
            return OAuthJson.Error(context, "invalid_client", "Unknown client, wrong client secret, or not the client's registered authentication method.",
                StatusCodes.Status401Unauthorized);
        }

        var now = time.GetUtcNow();
        var newRefresh = RefreshPrefix + Secrets.New();
        var expires = now.Add(Options.RefreshTokenLifetime);
        Grant grant;
        string accessScope;
        switch (grantType)
        {
            case "authorization_code":
            {
                var code = form["code"].ToString();
                var record = code.Length == 0 ? null : await store.TakeCodeAsync(Secrets.Hash(code), context.RequestAborted);
                if (record is null || record.Expires <= now || !string.Equals(record.ClientId, client.ClientId, StringComparison.Ordinal)
                    || !string.Equals(record.RedirectUri, form["redirect_uri"].ToString(), StringComparison.Ordinal))
                {
                    audit.TokenRefused(grantType, client.ClientId, "invalid_grant (code)");
                    return OAuthJson.Error(context, "invalid_grant", "The code is unknown, used, expired, for another client, or for another redirect_uri.");
                }
                if (!Pkce.Matches(form["code_verifier"].ToString(), record.CodeChallenge))
                {
                    audit.TokenRefused(grantType, client.ClientId, "invalid_grant (PKCE)");
                    return OAuthJson.Error(context, "invalid_grant", "code_verifier doesn't match the code_challenge.");
                }
                if (form["resource"].ToString() is { Length: > 0 } resource && !string.Equals(resource.TrimEnd('/'), record.Resource, StringComparison.Ordinal))
                {
                    audit.TokenRefused(grantType, client.ClientId, "invalid_target");
                    return OAuthJson.Error(context, "invalid_target", $"The code was issued for {record.Resource}.");
                }
                grant = new Grant
                {
                    GrantId = Secrets.New(),
                    ClientId = record.ClientId,
                    ClientName = record.ClientName,
                    UserName = record.UserName,
                    Roles = record.Roles,
                    Scope = record.Scope,
                    Resource = record.Resource,
                    RefreshHash = Secrets.Hash(newRefresh),
                    Created = now,
                    Expires = expires,
                };
                await store.AddGrantAsync(grant, context.RequestAborted);
                accessScope = grant.Scope;
                audit.TokenIssued(grant);
                break;
            }
            case "refresh_token":
            {
                var refresh = form["refresh_token"].ToString();
                var refreshHash = refresh.Length == 0 ? "" : Secrets.Hash(refresh);
                var found = refreshHash.Length == 0 ? null : await store.FindGrantByRefreshAsync(refreshHash, context.RequestAborted);
                if (found is null && refreshHash.Length > 0 && await store.FindGrantByPreviousRefreshAsync(refreshHash, context.RequestAborted) is { } reused)
                {
                    // A refresh token that was already rotated out: it leaked, or the client lost track (RFC 9700 4.14.2).
                    // The site can't tell which, so the connection ends, for whoever holds its current token too.
                    await store.DeleteGrantAsync(reused.GrantId, context.RequestAborted);
                    cache.Evict(reused.GrantId);
                    audit.Refresh(reused, "refused: a replaced refresh token was used again, connection deleted");
                    return OAuthJson.Error(context, "invalid_grant", "The refresh token was already used once, so the connection was revoked; connect again.");
                }
                if (found is null || !string.Equals(found.ClientId, client.ClientId, StringComparison.Ordinal) || found.Expires <= now)
                {
                    audit.TokenRefused(grantType, client.ClientId, "invalid_grant (refresh token)");
                    return OAuthJson.Error(context, "invalid_grant", "The refresh token is unknown, already used, expired, or revoked.");
                }
                var requested = form["scope"].ToString();
                if (requested.Length > 0 && !Scopes.Split(requested).IsSubsetOf(Scopes.Split(found.Scope)))
                {
                    return OAuthJson.Error(context, "invalid_scope", "A refresh can't add scopes the editor didn't approve.");
                }
                // The editor as they are now: someone who lost their editor role, or whose account was disabled, gets no
                // more tokens.
                var roles = await gate.CurrentRolesAsync(found.UserName, found.Roles, context.RequestAborted);
                if (await gate.RefusalAsync(EditorGate.Principal(found.UserName, roles, TokenService.AuthenticationType), Options, context.RequestAborted) is { } refusal)
                {
                    await store.DeleteGrantAsync(found.GrantId, context.RequestAborted);
                    cache.Evict(found.GrantId);
                    var why = refusal == GateRefusal.AccountDisabled ? "account disabled" : "no longer an editor";
                    audit.Refresh(found, $"refused: {why}, connection deleted");
                    return OAuthJson.Error(context, "invalid_grant", refusal == GateRefusal.AccountDisabled
                        ? $"The account {found.UserName} is disabled on this site."
                        : $"{found.UserName} is no longer an editor on this site.");
                }
                // A site that stopped allowing publishing takes the scope back from existing connections too.
                var allowed = Scopes.Grantable(found.Scope, Options);
                var scope = allowed.Length > 0 ? allowed : found.Scope;
                // Asking for less (RFC 6749 6) gives an access token for that much; the connection keeps what the editor
                // approved, and the next refresh may ask for it again.
                accessScope = Scopes.Narrowed(scope, requested);
                if (accessScope.Length == 0)
                {
                    return OAuthJson.Error(context, "invalid_scope", "None of the requested scopes is granted any more.");
                }
                // Refresh tokens rotate, as a compare-and-swap: of two refreshes with the same token only one gets new
                // tokens, and a connection revoked meanwhile isn't brought back.
                if (!await store.TryRotateRefreshAsync(found.GrantId, refreshHash, Secrets.Hash(newRefresh), expires, roles, scope, context.RequestAborted))
                {
                    audit.Refresh(found, "refused: refreshed at the same time, or revoked");
                    return OAuthJson.Error(context, "invalid_grant", "The refresh token is unknown, already used, expired, or revoked.");
                }
                grant = found with { Roles = roles, Scope = scope, RefreshHash = Secrets.Hash(newRefresh), PreviousRefreshHash = refreshHash, Expires = expires };
                audit.Refresh(grant, "rotated");
                break;
            }
            default:
                return OAuthJson.Error(context, "unsupported_grant_type", "Supported grant types: authorization_code, refresh_token.");
        }

        cache.Evict(grant.GrantId);
        var (access, expiresIn) = tokens.Issue(grant, accessScope);
        await maintenance.MaybeCleanUpAsync(store, now, context.RequestAborted);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = expiresIn,
            ["refresh_token"] = newRefresh,
            ["scope"] = accessScope,
        }, OAuthJson.Options);
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    /// <returns>The body, or null when it is longer than <paramref name="limit"/>.</returns>
    private static async Task<byte[]?> ReadLimitedAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[limit + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
        {
            length += read;
        }
        return length > limit ? null : buffer[..length];
    }

    private static string? String(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>An absent list is fine; a present one may only hold <paramref name="allowed"/> values and must hold <paramref name="required"/>.</summary>
    private static bool Lists(JsonElement root, string property, string[] allowed, string required)
    {
        if (!root.TryGetProperty(property, out var list))
        {
            return true;
        }
        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
        {
            return false;
        }
        var values = list.EnumerateArray().Select(v => v.GetString()!).ToList();
        return values.Contains(required) && values.All(allowed.Contains);
    }

    private static string Shorten(string value) => value.Length > 100 ? value[..100] + "…" : value;
}

/// <summary>
/// How a client identified itself at the token endpoint: HTTP Basic (<c>client_secret_basic</c>), form fields
/// (<c>client_secret_post</c>), or just a <c>client_id</c> (<c>none</c>). Only the client's registered method works.
/// </summary>
internal sealed record ClientCredentials(string ClientId, string? Secret, string Method)
{
    /// <returns>The credentials; null for a malformed Basic header, or one that names another client than the form.</returns>
    public static ClientCredentials? From(HttpRequest request, IFormCollection form)
    {
        var formId = form["client_id"].ToString();
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            }
            catch (FormatException)
            {
                return null;
            }
            var colon = decoded.IndexOf(':');
            if (colon <= 0 || form.ContainsKey("client_secret"))
            {
                return null;
            }
            // RFC 6749 2.3.1: both halves are form-urlencoded before they are joined.
            var id = WebUtility.UrlDecode(decoded[..colon]);
            var secret = WebUtility.UrlDecode(decoded[(colon + 1)..]);
            return formId.Length > 0 && formId != id ? null : new ClientCredentials(id, secret, ClientAuthMethods.SecretBasic);
        }
        return form.TryGetValue("client_secret", out var formSecret)
            ? new ClientCredentials(formId, formSecret.ToString(), ClientAuthMethods.SecretPost)
            : new ClientCredentials(formId, null, ClientAuthMethods.None);
    }

    /// <summary>The client's registered method, and for a confidential client its secret, compared as hashes in constant time.</summary>
    public bool Authenticates(RegisteredClient client)
    {
        if (!string.Equals(client.AuthMethod, Method, StringComparison.Ordinal))
        {
            return false;
        }
        return Method == ClientAuthMethods.None
            ? client.SecretHash.Length == 0
            : Secret is { Length: > 0 } && client.SecretHash.Length > 0 && Secrets.FixedTimeEquals(Secrets.Hash(Secret), client.SecretHash);
    }
}
