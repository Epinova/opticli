using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// The CMS's preview tokens (CMS 12.21 and later): a request with one, in the <c>preview_token</c> query parameter or an
/// <c>Authorization: preview_token …</c> header, is signed in as the editor the token was made for, by the CMS's own
/// authentication handler, which then is the request's default scheme. On CMS 12.21 only on the content the token was
/// made for; from CMS 12.22 (and on CMS 13) on any request, for the token's few minutes.
/// </summary>
/// <remarks>
/// Such a token is made to show unpublished content, and it ends up in URLs: in a browser's history, a shared link, an
/// assistant's transcript. It must never be the sign-in that connects an assistant (a connection lasts weeks, and may
/// write) or manages connections, so the consent and connections pages refuse a request that carries one
/// (<see cref="HtmlPage.PreviewToken"/>). The MCP endpoint itself only takes its own bearer tokens.
/// </remarks>
internal static class PreviewTokens
{
    /// <summary>The query parameter and <c>Authorization</c> scheme the CMS reads a preview token from.</summary>
    public const string Parameter = "preview_token";

    /// <summary>Whether <paramref name="request"/> carries a preview token, as the CMS looks for one.</summary>
    public static bool InRequest(HttpRequest request) =>
        request.Query[Parameter].Any(v => !string.IsNullOrEmpty(v))
        || (AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
            && header.Scheme.Equals(Parameter, StringComparison.OrdinalIgnoreCase));
}
