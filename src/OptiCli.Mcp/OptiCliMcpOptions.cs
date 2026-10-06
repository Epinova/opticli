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
    /// The hosts an app's return address (OAuth redirect URI) may be on, besides a loopback address on the editor's own
    /// machine (<c>http</c> or <c>https</c>, for desktop apps and Claude Code), which any app may use. Claude's own,
    /// <c>claude.ai</c> and <c>claude.com</c>, by default. The host must match exactly, ignoring case: no subdomains, no
    /// wildcards, https only. <c>["*"]</c> lets any https host through, for a site that knowingly accepts other web apps;
    /// the module logs a warning at startup then. In configuration, a list here replaces the default one.
    /// </summary>
    /// <remarks>
    /// Registration is open to anyone, and an app names itself: without this, someone could register "Claude" with a
    /// return address of their own, send an editor who is signed in to the site the link to connect it, and get the code
    /// the editor's Allow sends there, with the verifier that goes with it. Checked at registration and again at every
    /// authorization, for apps registered before the list changed and for apps with a metadata document too.
    /// </remarks>
    public string[] AllowedRedirectHosts { get; set; } = ["claude.ai", "claude.com"];

    /// <summary>
    /// How long an access token works. The editor's roles are looked up again on every refresh, so this is also how long
    /// a removed role can keep working.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a connection survives without being used: every refresh extends it by this much.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The longest a connection lasts, however often it is used: this long after the editor allowed it, its tokens stop
    /// working and the editor connects again, through the site's login. 30 days by default.
    /// </summary>
    /// <remarks>
    /// A refresh checks the editor's roles and account as the site knows them now, but for an external login (Entra ID,
    /// Opti ID, any OpenID Connect provider) the site knows little: the account is disabled in the identity provider,
    /// which the CMS never hears about, and the CMS only updates the editor's roles when they sign in. Without a limit,
    /// such an editor's connection would keep refreshing for as long as it is used. This is that limit, from when the
    /// editor allowed the connection (its <c>Created</c> time), checked at every refresh and on every access token. For a
    /// site with external logins, 1 to 7 days is a better choice. It can't be shorter than <see cref="AccessTokenLifetime"/>.
    /// </remarks>
    public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long after a refresh the refresh token it replaced, from the same client, is only refused. Later, or from
    /// another client, it revokes the connection (RFC 9700 4.14.2): it leaked, and the site can't tell who holds it. A
    /// client sends the old one again when it refreshed twice at once or retried after the answer got lost; revoking
    /// then would end the connection the other refresh just renewed. A minute by default; zero revokes on every reuse.
    /// </summary>
    public TimeSpan RefreshTokenReuseGrace { get; set; } = TimeSpan.FromMinutes(1);

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

    /// <summary>The <see cref="AllowedRedirectHosts"/> entry that lets any https host through.</summary>
    internal const string AnyRedirectHost = "*";

    /// <summary>The MCP endpoint's path: the resource access tokens are issued for.</summary>
    internal string McpPath => BasePath + "/mcp";

    /// <summary>When a connection made at <paramref name="created"/> ends (<see cref="ConnectionLifetime"/>), however often it is used.</summary>
    internal DateTimeOffset ConnectionEnds(DateTimeOffset created) =>
        DateTimeOffset.MaxValue - created <= ConnectionLifetime ? DateTimeOffset.MaxValue : created + ConnectionLifetime;

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
        if (AllowedRedirectHosts is null || AllowedRedirectHosts.Any(h => h != AnyRedirectHost && Uri.CheckHostName(h) is not (UriHostNameType.Dns or UriHostNameType.IPv4)))
        {
            return "AllowedRedirectHosts must list host names (claude.ai), without a scheme, port, path or wildcard, and no empty ones; or be [\"*\"] for any https host.";
        }
        if (AccessTokenLifetime <= TimeSpan.Zero || RefreshTokenLifetime <= TimeSpan.Zero)
        {
            return "AccessTokenLifetime and RefreshTokenLifetime must be positive.";
        }
        if (RefreshTokenReuseGrace < TimeSpan.Zero || RefreshTokenReuseGrace > AccessTokenLifetime)
        {
            return "RefreshTokenReuseGrace must be zero or more, and at most AccessTokenLifetime.";
        }
        if (AccessTokenLifetime > RefreshTokenLifetime)
        {
            return "AccessTokenLifetime can't be longer than RefreshTokenLifetime.";
        }
        if (ConnectionLifetime < AccessTokenLifetime)
        {
            return "ConnectionLifetime can't be shorter than AccessTokenLifetime.";
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
/// is the connection's, or the one the site's forwarded headers give behind a proxy (without them, every request seems
/// to come from the proxy, and all share one window); an IPv6 address counts by its /64, which one machine usually has
/// whole.
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
    public int RegisterPerMinute { get; set; } = 10;

    /// <summary>Token requests (code exchanges and refreshes) per client and IP address.</summary>
    public int TokenPerMinute { get; set; } = 60;

    /// <summary>Token requests per IP address, whatever the client: a ceiling checked before the request is read.</summary>
    public int TokenPerAddressPerMinute { get; set; } = 600;

    /// <summary>Authorization requests (the consent page and its post) per IP address; these come from editors' browsers.</summary>
    public int AuthorizePerMinute { get; set; } = 60;
}
