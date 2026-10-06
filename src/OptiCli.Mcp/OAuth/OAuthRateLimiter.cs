using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace OptiCli.Mcp.OAuth;

/// <summary>The endpoints with a rate limit of their own, and their limits a minute (see <see cref="OptiCliMcpRateLimits"/>).</summary>
internal sealed record OAuthRateLimits
{
    /// <summary>Registration stores a client: the one an anonymous caller could use to fill the database.</summary>
    public int RegisterPerMinute { get; init; } = 10;

    /// <summary>Token requests per client and address: code exchanges and refreshes, where secrets are guessed.</summary>
    public int TokenPerMinute { get; init; } = 60;

    /// <summary>Token requests per address, before the request is read.</summary>
    public int TokenPerAddressPerMinute { get; init; } = 600;

    /// <summary>Authorization requests (the page and the consent post), which may fetch a client metadata document.</summary>
    public int AuthorizePerMinute { get; init; } = 60;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    public static OAuthRateLimits From(OptiCliMcpRateLimits limits) => new()
    {
        RegisterPerMinute = limits.RegisterPerMinute,
        TokenPerMinute = limits.TokenPerMinute,
        TokenPerAddressPerMinute = limits.TokenPerAddressPerMinute,
        AuthorizePerMinute = limits.AuthorizePerMinute,
    };
}

/// <summary>
/// A fixed window per client IP and endpoint (for tokens also per client and IP), kept in memory inside the module, so
/// the site needn't call <c>UseRateLimiter</c> or know about it. Per instance: behind a load balancer each instance
/// counts on its own, which still bounds what one address can do. Behind a proxy the client IP is the one the site's
/// forwarded headers give. An IPv6 address counts by its /64 (<see cref="AddressKey"/>).
/// </summary>
internal sealed class OAuthRateLimiter : IDisposable
{
    public const string Register = "register";
    public const string Token = "token";
    public const string TokenClient = "token-client";
    public const string Authorize = "authorize";

    private readonly Dictionary<string, PartitionedRateLimiter<string>> _limiters;
    private readonly TimeSpan _window;

    public OAuthRateLimiter(OAuthRateLimits limits)
    {
        _window = limits.Window;
        _limiters = new Dictionary<string, PartitionedRateLimiter<string>>(StringComparer.Ordinal)
        {
            [Register] = Create(limits.RegisterPerMinute, limits.Window),
            [Token] = Create(limits.TokenPerAddressPerMinute, limits.Window),
            [TokenClient] = Create(limits.TokenPerMinute, limits.Window),
            [Authorize] = Create(limits.AuthorizePerMinute, limits.Window),
        };
    }

    /// <param name="client">For <see cref="TokenClient"/>: the client id the request names, counted with the address.</param>
    /// <returns>Null when the request may go on; a 429 with <c>Retry-After</c> when this address (and client) used up its window.</returns>
    public IResult? Check(string endpoint, HttpContext context, string? client = null)
    {
        var address = AddressKey(context.Connection.RemoteIpAddress);
        // A client id is at most a URL's length (RedirectUris.MaxLength) before it is refused anyway; longer ones share a key.
        var key = client is null ? address : $"{address} {(client.Length <= RedirectUris.MaxLength ? client : "(too long)")}";
        using var lease = _limiters[endpoint].AttemptAcquire(key);
        if (lease.IsAcquired)
        {
            return null;
        }
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : _window;
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new Dictionary<string, string>
            {
                ["error"] = "slow_down",
                ["error_description"] = client is null ? "Too many requests from this address; try again shortly." : "Too many token requests for this client; try again shortly.",
            },
            OAuthJson.Options,
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// What a request counts against: its IPv4 address (also one mapped into IPv6), or the /64 of its IPv6 address. One
    /// machine, or one customer of a provider, usually has a whole /64, so counting each address would let it take as many
    /// windows as it likes.
    /// </summary>
    internal static string AddressKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    private static PartitionedRateLimiter<string> Create(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
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
