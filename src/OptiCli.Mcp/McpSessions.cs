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
    /// <summary>Above this many, sessions idle for longer than the SDK keeps them are forgotten as new ones are made.</summary>
    internal const int PruneAbove = 1000;

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    /// <summary>The module's endpoint made <paramref name="session"/> for <paramref name="grant"/>'s connection.</summary>
    public void Made(string session, string grant)
    {
        Prune();
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
    /// gets "Session not found" either way, and starts a new one).
    /// </summary>
    private void Prune()
    {
#pragma warning disable MCP9006 // Obsolete: stateful mode, which only the site's own settings turn on.
        var idle = transport.Value.IdleTimeout;
#pragma warning restore MCP9006
        if (_sessions.Count < PruneAbove || idle <= TimeSpan.Zero)
        {
            return;
        }
        var before = time.GetUtcNow().UtcTicks - idle.Ticks;
        foreach (var (session, entry) in _sessions)
        {
            if (Interlocked.Read(ref entry.LastUsed) < before)
            {
                _sessions.TryRemove(session, out _);
            }
        }
    }

    private sealed class Entry(string grant, long lastUsed)
    {
        public readonly string Grant = grant;

        public long LastUsed = lastUsed;
    }
}
