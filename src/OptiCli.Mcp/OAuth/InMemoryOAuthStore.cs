using System.Collections.Concurrent;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// The store in process memory, for tests. Not for a site: a restart forgets every connection, and instances behind a
/// load balancer wouldn't share them.
/// </summary>
internal sealed class InMemoryOAuthStore : IOAuthStore
{
    private readonly ConcurrentDictionary<string, RegisteredClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    /// <summary>How many times <see cref="TouchGrantAsync"/> wrote, so tests can see the throttle.</summary>
    public int Touches;

    public Task<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(_clients.GetValueOrDefault(clientId));

    public Task AddClientAsync(RegisteredClient client, CancellationToken cancellationToken)
    {
        _clients[client.ClientId] = client;
        return Task.CompletedTask;
    }

    public Task AddCodeAsync(AuthorizationCode code, CancellationToken cancellationToken)
    {
        _codes[code.CodeHash] = code;
        return Task.CompletedTask;
    }

    public Task<AuthorizationCode?> TakeCodeAsync(string codeHash, CancellationToken cancellationToken) =>
        Task.FromResult(_codes.TryRemove(codeHash, out var code) ? code : null);

    public Task<Grant?> FindGrantAsync(string grantId, CancellationToken cancellationToken) =>
        Task.FromResult(_grants.TryGetValue(grantId, out var grant) ? WithUse(grant) : null);

    public Task<Grant?> FindGrantByRefreshAsync(string refreshHash, CancellationToken cancellationToken) =>
        Task.FromResult(_grants.Values.FirstOrDefault(g => g.RefreshHash == refreshHash) is { } grant ? WithUse(grant) : null);

    public Task<Grant?> FindGrantByPreviousRefreshAsync(string refreshHash, CancellationToken cancellationToken) =>
        Task.FromResult(_grants.Values.FirstOrDefault(g => g.PreviousRefreshHash.Length > 0 && g.PreviousRefreshHash == refreshHash) is { } grant ? WithUse(grant) : null);

    public Task AddGrantAsync(Grant grant, CancellationToken cancellationToken)
    {
        _grants[grant.GrantId] = grant with { LastUsed = null };
        return Task.CompletedTask;
    }

    /// <summary>For tests: runs as a refresh's compare-and-swap starts, after the refresh found the grant (a revocation, say).</summary>
    public Func<Task>? BeforeRotate;

    public async Task<bool> TryRotateRefreshAsync(string grantId, string currentRefreshHash, string newRefreshHash, DateTimeOffset issued, DateTimeOffset expires,
        IReadOnlyList<string> roles, string scope, CancellationToken cancellationToken)
    {
        if (BeforeRotate is { } beforeRotate)
        {
            await beforeRotate();
        }
        lock (GrantLocks.For(grantId))
        {
            if (!_grants.TryGetValue(grantId, out var grant) || grant.RefreshHash != currentRefreshHash)
            {
                return false;
            }
            _grants[grantId] = grant with
            {
                RefreshHash = newRefreshHash,
                PreviousRefreshHash = currentRefreshHash,
                RefreshIssued = issued,
                Expires = expires,
                Roles = roles,
                Scope = scope,
            };
            return true;
        }
    }

    public Task TouchGrantAsync(string grantId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Touches);
        _used[grantId] = at;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Grant>> ListGrantsAsync(string? userName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Grant>>(_grants.Values
            .Where(g => userName is null || string.Equals(g.UserName, userName, StringComparison.OrdinalIgnoreCase))
            .Select(WithUse)
            .OrderBy(g => g.Created)
            .ToList());

    public Task DeleteGrantAsync(string grantId, CancellationToken cancellationToken)
    {
        lock (GrantLocks.For(grantId))
        {
            _grants.TryRemove(grantId, out _);
            _used.TryRemove(grantId, out _);
        }
        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, DateTimeOffset unusedClientsBefore, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var code in _codes.Values.Where(c => c.Expires < now))
        {
            deleted += _codes.TryRemove(code.CodeHash, out _) ? 1 : 0;
        }
        foreach (var grant in _grants.Values.Where(g => g.Expires < now))
        {
            lock (GrantLocks.For(grant.GrantId))
            {
                deleted += _grants.TryRemove(grant.GrantId, out _) ? 1 : 0;
                _used.TryRemove(grant.GrantId, out _);
            }
        }
        var inUse = _grants.Values.Select(g => g.ClientId).Concat(_codes.Values.Select(c => c.ClientId)).ToHashSet(StringComparer.Ordinal);
        foreach (var client in _clients.Values.Where(c => c.Created < unusedClientsBefore && !inUse.Contains(c.ClientId)))
        {
            deleted += _clients.TryRemove(client.ClientId, out _) ? 1 : 0;
        }
        return Task.FromResult(deleted);
    }

    /// <summary>For tests: how many codes are waiting.</summary>
    public int CodeCount => _codes.Count;

    /// <summary>For tests: how many clients are registered.</summary>
    public int ClientCount => _clients.Count;

    private Grant WithUse(Grant grant) => grant with { LastUsed = _used.TryGetValue(grant.GrantId, out var at) ? at : null };
}
