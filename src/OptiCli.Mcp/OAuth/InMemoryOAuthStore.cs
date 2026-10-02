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

    public Task SaveGrantAsync(Grant grant, CancellationToken cancellationToken)
    {
        _grants[grant.GrantId] = grant with { LastUsed = null };
        return Task.CompletedTask;
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
        _grants.TryRemove(grantId, out _);
        _used.TryRemove(grantId, out _);
        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var code in _codes.Values.Where(c => c.Expires < now))
        {
            deleted += _codes.TryRemove(code.CodeHash, out _) ? 1 : 0;
        }
        foreach (var grant in _grants.Values.Where(g => g.Expires < now))
        {
            deleted += _grants.TryRemove(grant.GrantId, out _) ? 1 : 0;
            _used.TryRemove(grant.GrantId, out _);
        }
        return Task.FromResult(deleted);
    }

    /// <summary>For tests: how many codes are waiting.</summary>
    public int CodeCount => _codes.Count;

    private Grant WithUse(Grant grant) => grant with { LastUsed = _used.TryGetValue(grant.GrantId, out var at) ? at : null };
}
