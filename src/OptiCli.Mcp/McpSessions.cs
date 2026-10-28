using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace OptiCli.Mcp;

/// <summary>
/// The MCP sessions the module's endpoint made, each with the connection (grant) that made it. The module needs none: it
/// asks the SDK for stateless HTTP. But the SDK's transport settings are the whole application's, so a site whose own MCP
/// server sets <c>Stateless = false</c> makes the module's endpoint stateful too, and the SDK keeps every endpoint's
/// sessions in one store, found by ID alone. The module's endpoint takes only the sessions it made, and each only from
/// the connection that made it (<see cref="McpServerIsolation"/>).
/// </summary>
internal sealed class McpSessions(TimeProvider time, IOptions<HttpServerTransportOptions> transport)
{
    /// <summary>
    /// From this many on, sessions idle for longer than the SDK keeps them are forgotten as new ones are made; and never
    /// more than this, or the SDK's <c>MaxIdleSessionCount</c> if higher, are kept after a prune (<see cref="Prune"/>).
    /// </summary>
    internal const int PruneAbove = 1000;

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    private readonly object _pruning = new();

    /// <summary>The count at which <see cref="Made"/> prunes next: twice what the last prune left, so a prune's cost is spread over the sessions made since.</summary>
    private int _pruneAt = PruneAbove;

    public int Count => _sessions.Count;

    /// <summary>The module's endpoint made <paramref name="session"/> for <paramref name="grant"/>'s connection.</summary>
    public void Made(string session, string grant)
    {
        if (_sessions.Count >= Volatile.Read(ref _pruneAt))
        {
            Prune();
        }
        _sessions[session] = new Entry(grant, time.GetUtcNow().UtcTicks);
    }

    /// <summary>Whether the module's endpoint made <paramref name="session"/> for <paramref name="grant"/>'s connection.</summary>
    public bool Owns(string session, string? grant)
    {
        if (grant is null || !_sessions.TryGetValue(session, out var entry) || !string.Equals(entry.Grant, grant, StringComparison.Ordinal))
        {
            return false;
        }
        Interlocked.Exchange(ref entry.LastUsed, time.GetUtcNow().UtcTicks);
        return true;
    }

    /// <summary>The client ended <paramref name="session"/>.</summary>
    public void Ended(string session) => _sessions.TryRemove(session, out _);

    /// <summary>
    /// Forgets the sessions the SDK has dropped for being idle (it drops them itself; a client that comes back with one
    /// gets "Session not found" either way, and starts a new one), and, above the SDK's own limit of idle sessions
    /// (<c>MaxIdleSessionCount</c>, which it enforces the same way), the least recently used: an editor's assistant that
    /// makes sessions in a loop then costs a bounded amount of memory, and each new session a constant amount of work.
    /// </summary>
    private void Prune()
    {
        lock (_pruning)
        {
            if (_sessions.Count < _pruneAt)
            {
                return;
            }
#pragma warning disable MCP9006 // Obsolete: stateful mode, which only the site's own settings turn on.
            var idle = transport.Value.IdleTimeout;
            var keep = Math.Max(PruneAbove, transport.Value.MaxIdleSessionCount);
#pragma warning restore MCP9006
            if (idle > TimeSpan.Zero)
            {
                var before = time.GetUtcNow().UtcTicks - idle.Ticks;
                foreach (var (session, entry) in _sessions)
                {
                    if (Interlocked.Read(ref entry.LastUsed) < before)
                    {
                        _sessions.TryRemove(session, out _);
                    }
                }
            }
            if (_sessions.Count > keep)
            {
                var sessions = _sessions.ToArray();
                // When each was last used, read once: a request may use one meanwhile.
                var lastUsed = Array.ConvertAll(sessions, s => Interlocked.Read(ref s.Value.LastUsed));
                Array.Sort(lastUsed, sessions);
                foreach (var (session, _) in sessions.AsSpan(0, Math.Max(0, sessions.Length - keep)))
                {
                    _sessions.TryRemove(session, out _);
                }
            }
            Volatile.Write(ref _pruneAt, (int)Math.Min(int.MaxValue, Math.Max(PruneAbove, 2L * _sessions.Count)));
        }
    }

    private sealed class Entry(string grant, long lastUsed)
    {
        public readonly string Grant = grant;

        public long LastUsed = lastUsed;
    }
}
