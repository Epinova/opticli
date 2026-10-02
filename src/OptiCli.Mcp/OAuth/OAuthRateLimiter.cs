using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace OptiCli.Mcp.OAuth;

/// <summary>The endpoints with a rate limit of their own, and their limits per client IP and minute.</summary>
internal sealed record OAuthRateLimits
{
    /// <summary>Registration stores a client: the one an anonymous caller could use to fill the database.</summary>
    public int RegisterPerMinute { get; init; } = 20;

    /// <summary>Token requests: code exchanges and refreshes, where secrets are guessed.</summary>
    public int TokenPerMinute { get; init; } = 60;

    /// <summary>Authorization requests (the page and the consent post), which may fetch a client metadata document.</summary>
    public int AuthorizePerMinute { get; init; } = 60;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// A fixed window per client IP and endpoint, kept in memory inside the module, so the site needn't call
/// <c>UseRateLimiter</c> or know about it. Per instance: behind a load balancer each instance counts on its own, which
/// still bounds what one address can do. Behind a proxy the client IP is the one the site's forwarded headers give.
/// </summary>
internal sealed class OAuthRateLimiter : IDisposable
{
    public const string Register = "register";
    public const string Token = "token";
    public const string Authorize = "authorize";

    private readonly Dictionary<string, PartitionedRateLimiter<string>> _limiters;
    private readonly TimeSpan _window;

    public OAuthRateLimiter(OAuthRateLimits limits)
    {
        _window = limits.Window;
        _limiters = new Dictionary<string, PartitionedRateLimiter<string>>(StringComparer.Ordinal)
        {
            [Register] = Create(limits.RegisterPerMinute, limits.Window),
            [Token] = Create(limits.TokenPerMinute, limits.Window),
            [Authorize] = Create(limits.AuthorizePerMinute, limits.Window),
        };
    }

    /// <returns>Null when the request may go on; a 429 with <c>Retry-After</c> when this address used up its window.</returns>
    public IResult? Check(string endpoint, HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var lease = _limiters[endpoint].AttemptAcquire(address);
        if (lease.IsAcquired)
        {
            return null;
        }
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : _window;
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new Dictionary<string, string> { ["error"] = "slow_down", ["error_description"] = "Too many requests from this address; try again shortly." },
            OAuthJson.Options,
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static PartitionedRateLimiter<string> Create(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(address => RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }
}
