using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace OptiCli.Mcp.OAuth;

/// <summary>Claims the module puts on the editor's principal for a tool call.</summary>
public static class McpClaims
{
    /// <summary>The grant the access token belongs to.</summary>
    public const string Grant = "opticli:grant";

    /// <summary>The client's id.</summary>
    public const string Client = "opticli:client";

    /// <summary>The client's name, for audit logs.</summary>
    public const string ClientName = "opticli:client_name";

    /// <summary>Space-separated scopes the editor approved.</summary>
    public const string Scope = "scope";
}

/// <summary>
/// Access tokens are opaque to clients: the grant id, audience and expiry, protected with the site's Data Protection
/// keys (which every instance of a load-balanced site already shares, or its login cookies wouldn't work). Every use
/// checks the grant still exists, so deleting it revokes the token.
/// </summary>
internal sealed class TokenService(
    IDataProtectionProvider protection,
    IOAuthStore store,
    GrantCache cache,
    IOptions<OptiCliMcpOptions> options,
    TimeProvider time)
{
    public const string Prefix = "oc_";

    public const string AuthenticationType = "OptiCliMcp";

    /// <summary>A grant's last use is written at most this often per instance: often enough for the connections page.</summary>
    internal static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly IDataProtector _protector = protection.CreateProtector("OptiCli.Mcp.AccessToken.v1");

    private readonly ConcurrentDictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);

    /// <param name="Scope">The token's own scopes when a refresh asked for fewer than the grant has; null for the grant's.</param>
    private sealed record Payload(
        [property: JsonPropertyName("g")] string GrantId,
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("exp")] long Expires,
        [property: JsonPropertyName("scp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null);

    /// <param name="scope">
    /// The token's scopes, when fewer than the grant's (a refresh that asked for less, RFC 6749 6); the grant keeps its
    /// own, so the next refresh may ask for them again. Null or the grant's scopes for a token with all of them.
    /// </param>
    public (string Token, int ExpiresIn) Issue(Grant grant, string? scope = null)
    {
        var lifetime = options.Value.AccessTokenLifetime;
        var payload = new Payload(grant.GrantId, grant.Resource, time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds(),
            scope is null || scope == grant.Scope ? null : scope);
        return (Prefix + _protector.Protect(JsonSerializer.Serialize(payload)), (int)lifetime.TotalSeconds);
    }

    /// <returns>The editor the token stands for; null for a token that is malformed, expired, for another resource, or revoked.</returns>
    public async Task<ClaimsPrincipal?> ValidateAsync(string token, string resource, CancellationToken cancellationToken)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length > 4096)
        {
            return null;
        }
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token[Prefix.Length..]));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
        var now = time.GetUtcNow();
        if (payload is null || payload.Expires <= now.ToUnixTimeSeconds() || !string.Equals(payload.Audience, resource, StringComparison.Ordinal))
        {
            return null;
        }
        var grant = await cache.GetAsync(payload.GrantId, store, cancellationToken);
        if (grant is null || grant.Expires <= now || !string.Equals(grant.Resource, resource, StringComparison.Ordinal))
        {
            return null;
        }
        await TouchAsync(grant, now, cancellationToken);
        // Never more than the grant has now: a site that stopped allowing publishing narrowed it at the last refresh.
        return Principal(grant, payload.Scope is null ? grant.Scope : Scopes.Intersect(grant.Scope, payload.Scope));
    }

    /// <summary>The editor as the CMS sees them: their name and the roles they had at their last (re)authorization.</summary>
    /// <param name="scope">The access token's scopes; the grant's when null.</param>
    public static ClaimsPrincipal Principal(Grant grant, string? scope = null) => EditorGate.Principal(grant.UserName, grant.Roles, AuthenticationType,
    [
        new Claim(McpClaims.Grant, grant.GrantId),
        new Claim(McpClaims.Client, grant.ClientId),
        new Claim(McpClaims.ClientName, grant.ClientName),
        new Claim(McpClaims.Scope, scope ?? grant.Scope),
    ]);

    /// <summary>Records the use for the connections page, at most once a <see cref="TouchInterval"/> per grant.</summary>
    private async Task TouchAsync(Grant grant, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var last = _touched.TryGetValue(grant.GrantId, out var touched) ? touched : grant.LastUsed ?? DateTimeOffset.MinValue;
        if (now - last < TouchInterval)
        {
            return;
        }
        _touched[grant.GrantId] = now;
        if (_touched.Count > 10_000)
        {
            _touched.Clear();
        }
        await store.TouchGrantAsync(grant.GrantId, now, cancellationToken);
    }
}

/// <summary>
/// Grant lookups for access tokens, cached for at most <see cref="Lifetime"/>: a busy assistant makes many calls a
/// minute, and each would otherwise read the database. Revoking on this instance evicts at once; another instance of a
/// load-balanced site notices within the lifetime.
/// </summary>
internal sealed class GrantCache(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, (Grant? Grant, DateTimeOffset Until)> _entries = new(StringComparer.Ordinal);

    /// <summary>The grant, from the cache or the store. A missing grant is cached too: a revoked grant never comes back.</summary>
    public async Task<Grant?> GetAsync(string grantId, IOAuthStore store, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(grantId, out var entry) && entry.Until > now)
        {
            return entry.Grant;
        }
        var grant = await store.FindGrantAsync(grantId, cancellationToken);
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }
        _entries[grantId] = (grant, now.Add(Lifetime));
        return grant;
    }

    /// <summary>Forgets a grant that was revoked or changed, so this instance sees it at once.</summary>
    public void Evict(string grantId) => _entries.TryRemove(grantId, out _);
}
