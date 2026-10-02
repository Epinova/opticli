using Microsoft.Extensions.Logging;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Deletes expired codes and grants, and registered clients left unused, lazily, when tokens are issued, at most once
/// per <see cref="Interval"/> per instance: no scheduled job for the site to set up, and an idle site does no work.
/// </summary>
internal sealed class OAuthMaintenance(ILogger<OAuthMaintenance> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A client that registered (DCR) this long ago and has no connection, nor a sign-in under way, is deleted. A client
    /// whose last connection expired unused is one of them; by then the editor has to sign in again anyway, and an app
    /// that doesn't register again by itself when the site no longer knows it needs its connector removed and added
    /// again. Metadata document clients aren't stored, so this never touches them.
    /// </summary>
    public static readonly TimeSpan UnusedClientLifetime = TimeSpan.FromDays(30);

    private long _lastRun = DateTimeOffset.MinValue.UtcTicks;

    public async Task MaybeCleanUpAsync(IOAuthStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var last = Interlocked.Read(ref _lastRun);
        if (now.UtcTicks - last < Interval.Ticks || Interlocked.CompareExchange(ref _lastRun, now.UtcTicks, last) != last)
        {
            return;
        }
        try
        {
            var deleted = await store.DeleteExpiredAsync(now, now - UnusedClientLifetime, cancellationToken);
            if (deleted > 0)
            {
                logger.LogDebug("Deleted {Count} expired MCP codes and grants and unused clients.", deleted);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The token was issued: a failed cleanup mustn't fail it. The next one tries again.
            logger.LogWarning(e, "Deleting expired MCP codes and grants and unused clients failed.");
        }
    }
}
