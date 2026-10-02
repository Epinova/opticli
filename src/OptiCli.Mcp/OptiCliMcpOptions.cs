namespace OptiCli.Mcp;

/// <summary>
/// Settings for the opticli MCP module. Bound from the <c>OptiCli:Mcp</c> configuration section, then from the
/// delegate passed to <see cref="OptiCliMcpExtensions.AddOptiCliMcp"/>, which wins.
/// </summary>
public sealed class OptiCliMcpOptions
{
    /// <summary>The configuration section the options are read from.</summary>
    public const string SectionName = "OptiCli:Mcp";

    /// <summary>
    /// Where the module lives: the MCP endpoint is <c>{BasePath}/mcp</c> and the authorization server's issuer is
    /// <c>{origin}{BasePath}</c>. Defaults to <c>/episerver/opticli</c>, so a site's own MCP server and OAuth metadata at
    /// the root stay untouched. <c>""</c> puts the module at the root of a host of its own, and then needs
    /// <see cref="RequireHost"/>.
    /// </summary>
    public string BasePath { get; set; } = "/episerver/opticli";

    /// <summary>
    /// Only answer on this host name (<c>host</c> or <c>host:port</c>, e.g. an editors' host), so the module can't be
    /// reached through the public one. Null answers on every host.
    /// </summary>
    /// <remarks>
    /// With <see cref="BasePath"/> <c>""</c> (dedicated-host mode) the issuer has no path, which suits clients that
    /// don't handle an issuer with one.
    /// </remarks>
    public string? RequireHost { get; set; }

    /// <summary>
    /// Roles that may connect an AI assistant at all; the CMS's access rights then decide per item. CMS virtual roles
    /// (<c>CmsEditors</c>, <c>CmsAdmins</c>) count. In configuration, a list here replaces the default one.
    /// </summary>
    public string[] AllowedRoles { get; set; } = ["WebEditors", "WebAdmins", "CmsEditors", "CmsAdmins", "Administrators"];

    /// <summary>
    /// How long an access token works. The editor's roles are looked up again on every refresh, so this is also how long
    /// a removed role can keep working.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a connection survives without being used: every refresh extends it by this much.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether an assistant may publish, unpublish and schedule publishing at all (the <c>content:publish</c> scope).
    /// Off by default: the site owner opts in, and the editor's consent and access rights still apply.
    /// </summary>
    public bool AllowPublish { get; set; }

    /// <summary>
    /// Whether an assistant may delete content (always to the recycle bin). Off by default: the site owner opts in, and
    /// the editor's access rights still apply.
    /// </summary>
    public bool AllowDelete { get; set; }

    /// <summary>
    /// The largest media file an assistant may upload, in bytes: 10 MB by default, 50 MB at most. The MCP endpoint takes
    /// requests up to this size as base64 plus 1 MB; on IIS, raise <c>maxAllowedContentLength</c> (30 MB by default) to
    /// match for more than about 20 MB.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>How many requests the OAuth endpoints take a minute (<see cref="OptiCliMcpRateLimits"/>).</summary>
    public OptiCliMcpRateLimits RateLimits { get; set; } = new();

    /// <summary>The MCP endpoint's path: the resource access tokens are issued for.</summary>
    internal string McpPath => BasePath + "/mcp";

    /// <summary>Dedicated-host mode: the module owns the host's root, so the root well-known documents are its own.</summary>
    internal bool DedicatedHost => BasePath.Length == 0;

    /// <returns>What is wrong with the options, or null when they are usable.</returns>
    internal string? Problem()
    {
        if (BasePath.Length > 0 && (!BasePath.StartsWith('/') || BasePath.EndsWith('/') || BasePath.Contains("//", StringComparison.Ordinal)
            || BasePath.Any(c => c is '?' or '#' or '{' or '}' or '*' || char.IsWhiteSpace(c))))
        {
            return $"BasePath '{BasePath}' must start with '/', not end with one, and be a plain path (or \"\" for a dedicated host).";
        }
        if (BasePath.Length == 0 && string.IsNullOrWhiteSpace(RequireHost))
        {
            return "BasePath \"\" puts the module at the site root, which needs RequireHost: a host name of its own.";
        }
        if (AllowedRoles.Length == 0 || AllowedRoles.Any(string.IsNullOrWhiteSpace))
        {
            return "AllowedRoles must name at least one role, and no empty ones.";
        }
        if (AccessTokenLifetime <= TimeSpan.Zero || RefreshTokenLifetime <= TimeSpan.Zero)
        {
            return "AccessTokenLifetime and RefreshTokenLifetime must be positive.";
        }
        if (AccessTokenLifetime > RefreshTokenLifetime)
        {
            return "AccessTokenLifetime can't be longer than RefreshTokenLifetime.";
        }
        if (RateLimits is null || RateLimits.RegisterPerMinute <= 0 || RateLimits.TokenPerMinute <= 0 || RateLimits.TokenPerAddressPerMinute <= 0 || RateLimits.AuthorizePerMinute <= 0)
        {
            return "RateLimits: every limit must be positive.";
        }
        if (MaxUploadBytes is <= 0 or > Protocol.UploadRequest.MaxBytes)
        {
            return $"MaxUploadBytes must be positive and at most {Protocol.UploadRequest.MaxBytes / (1024 * 1024)} MB, the most opticli uploads.";
        }
        return null;
    }
}

/// <summary>
/// The OAuth endpoints' rate limits: requests a minute, a fixed window, counted per instance (behind a load balancer
/// each instance counts on its own). Over the limit, a request gets 429 with <c>Retry-After</c>. The client IP address
/// is the connection's, or the one the site's forwarded headers give behind a proxy.
/// </summary>
/// <remarks>
/// claude.ai's connectors call the token and register endpoints from Anthropic's cloud (<c>160.79.104.0/21</c>), so
/// every editor of a site who uses claude.ai shares those addresses. That is why the token endpoint counts per client
/// as well as per address, and why the per-address ceilings are well above what one client needs.
/// </remarks>
public sealed class OptiCliMcpRateLimits
{
    /// <summary>
    /// Client registrations (dynamic client registration) per IP address. Clients with a metadata document, as Claude
    /// has, don't register at all; others register once per connector, not per sign-in.
    /// </summary>
    public int RegisterPerMinute { get; set; } = 60;

    /// <summary>Token requests (code exchanges and refreshes) per client and IP address.</summary>
    public int TokenPerMinute { get; set; } = 60;

    /// <summary>Token requests per IP address, whatever the client: a ceiling checked before the request is read.</summary>
    public int TokenPerAddressPerMinute { get; set; } = 600;

    /// <summary>Authorization requests (the consent page and its post) per IP address; these come from editors' browsers.</summary>
    public int AuthorizePerMinute { get; set; } = 60;
}
