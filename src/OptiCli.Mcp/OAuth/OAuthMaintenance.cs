using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Deletes expired codes and grants, and registered clients left unused, lazily, when tokens are issued, at most once
/// per <see cref="Interval"/> per instance: no scheduled job for the site to set up, and an idle site does no work. Each
/// run is in the background, so the token response never waits for it, and deletes at most
/// <see cref="MaxDeletedPerRun"/>; what is left, the next run deletes. Registration is open to anyone, so it also keeps
/// the number of registered clients nobody uses below <see cref="MaxUnusedClients"/>.
/// </summary>
internal sealed class OAuthMaintenance(ILogger<OAuthMaintenance> logger, IHostApplicationLifetime lifetime)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A client that registered (DCR) this long ago and has no connection, nor a sign-in under way, is deleted. A real app
    /// signs in right after it registers; one that doesn't within a day never will. A client whose last connection
    /// expired unused is one of them too; by then the editor has to sign in again anyway, and an app that doesn't register
    /// again by itself when the site no longer knows it needs its connector removed and added again. Metadata document
    /// clients aren't stored, so this never touches them.
    /// </summary>
    public static readonly TimeSpan UnusedClientLifetime = TimeSpan.FromDays(1);

    /// <summary>
    /// The most registered clients without a connection or a sign-in under way the site keeps: past it, registration
    /// answers 429 until the cleanup has deleted some. Far above what a site's editors register in a day, whose apps
    /// register once per connector (and Claude, with its metadata document, not at all).
    /// </summary>
    public const int MaxUnusedClients = 5000;

    /// <summary>The most codes, grants and clients one run deletes, so a run stays short whatever has piled up.</summary>
    public const int MaxDeletedPerRun = 500;

    /// <summary>How long registration is refused once it is full (its <c>Retry-After</c>), and how often that is logged.</summary>
    public static readonly TimeSpan FullFor = TimeSpan.FromMinutes(10);

    private long _lastRun = DateTimeOffset.MinValue.UtcTicks;
    private long _lastFullWarning = DateTimeOffset.MinValue.UtcTicks;
    private int _running;

    /// <summary>The cleanup started last, for tests to wait for; a completed task before the first.</summary>
    internal Task LastRun { get; private set; } = Task.CompletedTask;

    /// <summary>Starts a cleanup in the background, unless one ran less than <see cref="Interval"/> ago or is still running.</summary>
    public void MaybeCleanUp(IOAuthStore store, DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastRun);
        if (now.UtcTicks - last < Interval.Ticks || Interlocked.CompareExchange(ref _lastRun, now.UtcTicks, last) != last)
        {
            return;
        }
        StartCleanUp(store, now);
    }

    /// <summary>
    /// Whether registration is full: <see cref="MaxUnusedClients"/> clients nobody uses. Logged as a warning at most once
    /// per <see cref="FullFor"/>, and a cleanup is started, since on a site where nobody signs in none would run.
    /// </summary>
    public async Task<bool> RegistrationsFullAsync(IOAuthStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unused = await UnusedClientsAsync(store, now, cancellationToken);
        if (unused < MaxUnusedClients)
        {
            return false;
        }
        var warned = Interlocked.Read(ref _lastFullWarning);
        if (now.UtcTicks - warned >= FullFor.Ticks && Interlocked.CompareExchange(ref _lastFullWarning, now.UtcTicks, warned) == warned)
        {
            logger.LogWarning(
                "MCP client registration is refused: {Count} registered clients have neither a connection nor a sign-in under way (at most {Max}). Each is deleted {Lifetime} after it registered; many at once usually means someone is registering clients to fill the database.",
                unused, MaxUnusedClients, UnusedClientLifetime);
        }
        StartCleanUp(store, now);
        // Counted again next time: the cleanup may have made room.
        _unusedCounted = null;
        return true;
    }

    /// <summary>
    /// How many unused clients the store has, counted at most once per <see cref="Interval"/> (registration is open to
    /// anyone, so it mustn't query the store each time), plus the registrations since (<see cref="Registered"/>).
    /// </summary>
    private async Task<int> UnusedClientsAsync(IOAuthStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_unusedCounted is { } counted && now - counted.At < Interval)
        {
            return counted.Count + Volatile.Read(ref _registeredSince);
        }
        var count = await store.CountUnusedClientsAsync(cancellationToken);
        Interlocked.Exchange(ref _registeredSince, 0);
        _unusedCounted = (count, now);
        return count;
    }

    /// <summary>A client registered: one more unused client until the next count.</summary>
    public void Registered() => Interlocked.Increment(ref _registeredSince);

    private (int Count, DateTimeOffset At)? _unusedCounted;
    private int _registeredSince;

    private void StartCleanUp(IOAuthStore store, DateTimeOffset now)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }
        // Without the request's execution context: the run outlives the request, and must use neither its HttpContext
        // nor its services nor the CMS's database executor for it (which isn't thread-safe), as a scheduled job doesn't.
        AsyncFlowControl? suppressed = ExecutionContext.IsFlowSuppressed() ? null : ExecutionContext.SuppressFlow();
        try
        {
            LastRun = Task.Run(RunAsync);
        }
        finally
        {
            suppressed?.Undo();
        }

        async Task RunAsync()
        {
            try
            {
                var deleted = await store.DeleteExpiredAsync(now, now - UnusedClientLifetime, MaxDeletedPerRun, lifetime.ApplicationStopping);
                if (deleted > 0)
                {
                    logger.LogDebug("Deleted {Count} expired MCP codes and grants and unused clients.", deleted);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The token was issued: a failed cleanup changes nothing for it. The next one tries again.
                logger.LogWarning(e, "Deleting expired MCP codes and grants and unused clients failed.");
            }
            catch (OperationCanceledException)
            {
                // The site is stopping.
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }
}
