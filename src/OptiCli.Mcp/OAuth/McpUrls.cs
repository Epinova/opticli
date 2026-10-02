using Microsoft.AspNetCore.Http;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// The module's paths and absolute URLs. Absolute ones come from the request's own scheme and host, so one site answers
/// on all its host names; behind a proxy that needs the site's forwarded headers set up, as its login already does.
/// </summary>
internal static class McpUrls
{
    public static string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>The MCP endpoint: the resource tokens are issued for (RFC 8707) and checked against.</summary>
    public static string Resource(HttpRequest request, OptiCliMcpOptions options) => Origin(request) + options.McpPath;

    /// <summary><c>{origin}{BasePath}</c>; the bare origin in dedicated-host mode.</summary>
    public static string Issuer(HttpRequest request, OptiCliMcpOptions options) => Origin(request) + options.BasePath;

    public static string AuthorizePath(OptiCliMcpOptions options) => options.BasePath + "/oauth/authorize";

    public static string TokenPath(OptiCliMcpOptions options) => options.BasePath + "/oauth/token";

    public static string RegisterPath(OptiCliMcpOptions options) => options.BasePath + "/oauth/register";

    public static string ConnectionsPath(OptiCliMcpOptions options) => options.BasePath + "/connections";

    /// <summary>RFC 9728's path-inserted form, so a site's own root document is never shadowed.</summary>
    public static string ResourceMetadataPath(OptiCliMcpOptions options) => "/.well-known/oauth-protected-resource" + options.McpPath;

    public static string ResourceMetadata(HttpRequest request, OptiCliMcpOptions options) => Origin(request) + ResourceMetadataPath(options);

    /// <summary>
    /// Every path the protected resource metadata is served on: the path-inserted one, plus the root one in
    /// dedicated-host mode, which some clients try first.
    /// </summary>
    public static IEnumerable<string> ResourceMetadataPaths(OptiCliMcpOptions options) =>
        options.DedicatedHost ? [ResourceMetadataPath(options), "/.well-known/oauth-protected-resource"] : [ResourceMetadataPath(options)];

    /// <summary>
    /// Every path the authorization server metadata is served on: RFC 8414's path-inserted form, OpenID Connect
    /// discovery's path-inserted form, and OpenID Connect's appended form, which some clients use for an issuer with a
    /// path. In dedicated-host mode the issuer has no path, and these are the root documents (two, not three).
    /// </summary>
    public static IEnumerable<string> ServerMetadataPaths(OptiCliMcpOptions options) => new[]
    {
        "/.well-known/oauth-authorization-server" + options.BasePath,
        "/.well-known/openid-configuration" + options.BasePath,
        options.BasePath + "/.well-known/openid-configuration",
    }.Distinct(StringComparer.Ordinal);
}
