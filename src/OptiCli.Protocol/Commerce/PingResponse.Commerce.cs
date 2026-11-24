namespace OptiCli.Protocol;

/// <summary>What <c>ping</c> says about Commerce. Not compiled into the MCP module, which has no Commerce.</summary>
public sealed partial record PingResponse
{
    /// <summary>The site's Optimizely Commerce, and whether opticli's Commerce agent runs in it; null for a site without Commerce (or an agent older than this field).</summary>
    public CommerceStatus? Commerce { get; init; }
}

/// <summary>A Commerce site, as the agent found it.</summary>
/// <param name="Version">The site's EPiServer.Commerce.Core version, from its build's <c>.deps.json</c>.</param>
/// <param name="Agent">Whether opticli's Commerce agent loaded into the site; when false, <paramref name="Error"/> says why, and the Commerce commands don't work.</param>
/// <param name="Error">Why the Commerce agent didn't load.</param>
public sealed record CommerceStatus(string Version, bool Agent, string? Error = null);
