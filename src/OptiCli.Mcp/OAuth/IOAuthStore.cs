namespace OptiCli.Mcp.OAuth;

/// <summary>Client authentication methods at the token endpoint (RFC 7591 2).</summary>
public static class ClientAuthMethods
{
    /// <summary>A public client: no secret, PKCE alone proves it started the sign-in.</summary>
    public const string None = "none";

    /// <summary>The secret as a <c>client_secret</c> form field.</summary>
    public const string SecretPost = "client_secret_post";

    /// <summary>The secret in an HTTP Basic <c>Authorization</c> header.</summary>
    public const string SecretBasic = "client_secret_basic";

    internal static readonly string[] All = [None, SecretPost, SecretBasic];
}

/// <summary>A client that registered itself (RFC 7591).</summary>
public sealed record RegisteredClient
{
    /// <summary>The id the module gave the client: <c>mcp_…</c>, never a URL.</summary>
    public required string ClientId { get; init; }

    /// <summary>The name the client gave itself, shown on the consent page. Nobody checked it.</summary>
    public required string ClientName { get; init; }

    /// <summary>The exact redirect URIs the client may use.</summary>
    public required IReadOnlyList<string> RedirectUris { get; init; }

    /// <summary>One of <see cref="ClientAuthMethods"/>; the token endpoint only accepts this one.</summary>
    public required string AuthMethod { get; init; }

    /// <summary>SHA-256 of the client secret; empty for a public client.</summary>
    public string SecretHash { get; init; } = "";

    /// <summary>When it registered.</summary>
    public DateTimeOffset Created { get; init; }
}

/// <summary>A one-time authorization code, waiting for the client to exchange it.</summary>
public sealed record AuthorizationCode
{
    /// <summary>SHA-256 of the code; the code itself only ever travels to the client.</summary>
    public required string CodeHash { get; init; }

    /// <summary>The client it was issued to.</summary>
    public required string ClientId { get; init; }

    /// <summary>The client's name, kept for the connections page (a metadata document client isn't stored).</summary>
    public required string ClientName { get; init; }

    /// <summary>The redirect URI the authorization request used; the token request must send the same one.</summary>
    public required string RedirectUri { get; init; }

    /// <summary>The PKCE S256 challenge.</summary>
    public required string CodeChallenge { get; init; }

    /// <summary>The editor who approved it.</summary>
    public required string UserName { get; init; }

    /// <summary>The editor's roles when they approved, as the role provider has them.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>Space-separated scopes the editor approved.</summary>
    public required string Scope { get; init; }

    /// <summary>The MCP endpoint URL the tokens are for.</summary>
    public required string Resource { get; init; }

    /// <summary>When it stops working: minutes after it was issued.</summary>
    public required DateTimeOffset Expires { get; init; }
}

/// <summary>
/// What an editor allowed a client: a connection. Refresh tokens rotate on it, access tokens point to it, and deleting it
/// revokes both.
/// </summary>
public sealed record Grant
{
    /// <summary>Random and unguessable; access tokens carry it, protected.</summary>
    public required string GrantId { get; init; }

    /// <summary>The client it was issued to.</summary>
    public required string ClientId { get; init; }

    /// <summary>The client's name, as it was when the editor approved it.</summary>
    public required string ClientName { get; init; }

    /// <summary>The editor it acts as.</summary>
    public required string UserName { get; init; }

    /// <summary>The editor's roles, looked up again on every refresh.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>Space-separated scopes the editor approved.</summary>
    public required string Scope { get; init; }

    /// <summary>The MCP endpoint URL the tokens are for.</summary>
    public required string Resource { get; init; }

    /// <summary>SHA-256 of the current refresh token; an older one no longer matches.</summary>
    public required string RefreshHash { get; init; }

    /// <summary>
    /// SHA-256 of the refresh token the current one replaced; empty before the first refresh. A rotated-out token that
    /// comes back means it leaked, or the client is confused: either way the grant ends (RFC 9700 4.14.2). Only the
    /// one before is kept: older tokens are just unknown.
    /// </summary>
    public string PreviousRefreshHash { get; init; } = "";

    /// <summary>When the editor approved it.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>When it stops working unless refreshed before then.</summary>
    public required DateTimeOffset Expires { get; init; }

    /// <summary>When a token for it was last used, to the minute; null before the first use.</summary>
    public DateTimeOffset? LastUsed { get; init; }
}

/// <summary>
/// The authorization server's state. The site's store is the CMS database (Dynamic Data Store), so every instance
/// behind a load balancer sees the same clients, codes and grants. Secrets arrive here already hashed.
/// </summary>
public interface IOAuthStore
{
    /// <returns>The registered client; null for an unknown id.</returns>
    Task<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>Adds a newly registered client.</summary>
    Task AddClientAsync(RegisteredClient client, CancellationToken cancellationToken);

    /// <summary>Adds a newly issued authorization code.</summary>
    Task AddCodeAsync(AuthorizationCode code, CancellationToken cancellationToken);

    /// <summary>Finds and deletes a code in one go: a code works once, even when it then turns out to be invalid.</summary>
    /// <returns>The code; null when it is unknown or already taken.</returns>
    Task<AuthorizationCode?> TakeCodeAsync(string codeHash, CancellationToken cancellationToken);

    /// <returns>The grant; null for an unknown (or revoked) id.</returns>
    Task<Grant?> FindGrantAsync(string grantId, CancellationToken cancellationToken);

    /// <returns>The grant whose current refresh token has this hash; null for none.</returns>
    Task<Grant?> FindGrantByRefreshAsync(string refreshHash, CancellationToken cancellationToken);

    /// <returns>The grant whose previous refresh token (<see cref="Grant.PreviousRefreshHash"/>) has this hash; null for none.</returns>
    Task<Grant?> FindGrantByPreviousRefreshAsync(string refreshHash, CancellationToken cancellationToken);

    /// <summary>Adds a new grant, for a code just exchanged.</summary>
    Task AddGrantAsync(Grant grant, CancellationToken cancellationToken);

    /// <summary>
    /// A refresh: replaces the grant's refresh token, if it is still <paramref name="currentRefreshHash"/>, and sets
    /// what the refresh looked up again, as one compare-and-swap. Of two refreshes with the same token only one
    /// succeeds, and a grant deleted meanwhile (revoked) stays deleted: this never adds one.
    /// </summary>
    /// <param name="newRefreshHash">Becomes <see cref="Grant.RefreshHash"/>; <paramref name="currentRefreshHash"/> becomes <see cref="Grant.PreviousRefreshHash"/>.</param>
    /// <returns>False when the grant is gone, or its refresh token is no longer <paramref name="currentRefreshHash"/>.</returns>
    Task<bool> TryRotateRefreshAsync(string grantId, string currentRefreshHash, string newRefreshHash, DateTimeOffset expires,
        IReadOnlyList<string> roles, string scope, CancellationToken cancellationToken);

    /// <summary>Records when a grant was last used, without touching the rest of it (a refresh may be saving it).</summary>
    Task TouchGrantAsync(string grantId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <returns>The grants of <paramref name="userName"/> (case-insensitive), or every grant when null.</returns>
    Task<IReadOnlyList<Grant>> ListGrantsAsync(string? userName, CancellationToken cancellationToken);

    /// <summary>Deletes a grant, which revokes its tokens. Nothing happens for an unknown id.</summary>
    Task DeleteGrantAsync(string grantId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes codes and grants that expired before <paramref name="now"/>, and registered clients created before
    /// <paramref name="unusedClientsBefore"/> that have neither a grant nor a code waiting: registration is open to
    /// anyone, so what it stores must not pile up.
    /// </summary>
    /// <returns>How many were deleted.</returns>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, DateTimeOffset unusedClientsBefore, CancellationToken cancellationToken);
}

/// <summary>
/// Locks for changing one grant within this process: a refresh's compare-and-swap and a revocation take the same one,
/// so neither undoes the other. Striped, so memory stays bounded however many grants there are.
/// </summary>
internal static class GrantLocks
{
    private static readonly object[] Stripes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    public static object For(string grantId) => Stripes[(int)((uint)StringComparer.Ordinal.GetHashCode(grantId) % (uint)Stripes.Length)];
}
