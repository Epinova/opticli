using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Bearer tokens on the MCP endpoint, and only there: the site's default scheme is left as it is. A missing or bad token
/// gets a plain 401 that points to the protected resource metadata (RFC 9728), never a redirect to the site's login
/// page: that is how an MCP client finds where to sign in.
/// </summary>
internal sealed class McpBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens,
    IOptions<OptiCliMcpOptions> options)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "OptiCliMcpBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count != 1 || header.ToString() is not { } value || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        var principal = await tokens.ValidateAsync(value["Bearer ".Length..].Trim(), McpUrls.Resource(Request, options.Value), Context.RequestAborted);
        return principal is null
            ? AuthenticateResult.Fail("invalid_token")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <remarks>
    /// With a small JSON body, and the site's status code pages off (<see cref="StatusCodePages"/>): an empty 401 is what
    /// a site's custom error pages replace, and the client then never sees the header that tells it where to sign in.
    /// </remarks>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        var invalid = result.Failure is not null;
        var error = invalid ? ", error=\"invalid_token\", error_description=\"The access token is invalid, expired or revoked.\"" : "";
        StatusCodePages.Skip(Context);
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.WWWAuthenticate =
            $"Bearer resource_metadata=\"{McpUrls.ResourceMetadata(Request, options.Value)}\", scope=\"{string.Join(' ', Scopes.Supported(options.Value))}\"{error}";
        await WriteErrorAsync(invalid ? "invalid_token" : "unauthorized", invalid
            ? "The access token is invalid, expired or revoked."
            : "This MCP endpoint needs an OAuth access token: the WWW-Authenticate header's resource_metadata says where to get one.");
    }

    /// <summary>Signed in, but the connection wasn't granted what the endpoint needs (RFC 6750 3.1).</summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        StatusCodePages.Skip(Context);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.WWWAuthenticate =
            $"Bearer error=\"insufficient_scope\", scope=\"{Scopes.Read}\", resource_metadata=\"{McpUrls.ResourceMetadata(Request, options.Value)}\"";
        return WriteErrorAsync("insufficient_scope", $"The connection wasn't granted {Scopes.Read}; connect again.");
    }

    /// <summary>The error as JSON (RFC 6750 allows a body), in the OAuth error response's shape.</summary>
    private Task WriteErrorAsync(string error, string description) => Response.HasStarted
        ? Task.CompletedTask
        : Response.WriteAsJsonAsync(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, OAuthJson.Options, Context.RequestAborted);
}
