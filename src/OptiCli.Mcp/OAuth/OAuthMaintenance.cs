using Microsoft.Extensions.Logging;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Deletes expired codes and grants lazily, when tokens are issued, at most once per <see cref="Interval"/> per
/// instance: no scheduled job for the site to set up, and an idle site does no work.
/// </summary>
internal sealed class OAuthMaintenance(ILogger<OAuthMaintenance> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

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
            var deleted = await store.DeleteExpiredAsync(now, cancellationToken);
            if (deleted > 0)
            {
                logger.LogDebug("Deleted {Count} expired MCP codes and grants.", deleted);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The token was issued: a failed cleanup mustn't fail it. The next one tries again.
            logger.LogWarning(e, "Deleting expired MCP codes and grants failed.");
        }
    }
}
