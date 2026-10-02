using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Finds the client behind a <c>client_id</c>: one that registered itself (DCR), or an HTTPS URL whose metadata
/// document describes the client (CIMD), fetched with care: public addresses only, checked on the connected socket, no
/// redirects or proxies, at most <see cref="ClientMetadataDocument.MaxBytes"/> in <see cref="FetchTimeout"/>, and cached
/// for <see cref="CacheFor"/>.
/// </summary>
internal sealed class ClientResolver : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    /// <summary>A document that couldn't be used isn't fetched again for this long, so a bad client_id can't keep the site fetching.</summary>
    public static readonly TimeSpan FailureCacheFor = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    private const int MaxCached = 1000;

    /// <summary>Set on a request for a loopback document in Development, the only case the socket check lets loopback through.</summary>
    internal static readonly HttpRequestOptionsKey<bool> AllowLoopback = new("OptiCli.Mcp.AllowLoopback");

    private readonly IOAuthStore _store;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ClientResolver> _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (RegisteredClient? Client, DateTimeOffset Until)> _documents = new(StringComparer.Ordinal);

    public ClientResolver(IOAuthStore store, IHostEnvironment environment, ILogger<ClientResolver> logger, TimeProvider time)
        : this(store, environment, logger, time, CreateHandler())
    {
    }

    internal ClientResolver(IOAuthStore store, IHostEnvironment environment, ILogger<ClientResolver> logger, TimeProvider time, HttpMessageHandler handler)
    {
        _store = store;
        _environment = environment;
        _logger = logger;
        _time = time;
        _http = new HttpClient(handler) { Timeout = FetchTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("opticli-mcp");
    }

    /// <returns>The client; null for an unknown one, or a metadata document that can't be fetched or used.</returns>
    public async Task<RegisteredClient?> ResolveAsync(string clientId, CancellationToken cancellationToken)
    {
        if (clientId.Length is 0 or > RedirectUris.MaxLength)
        {
            return null;
        }
        if (!ClientMetadataDocument.IsUrl(clientId))
        {
            return await _store.FindClientAsync(clientId, cancellationToken);
        }
        var now = _time.GetUtcNow();
        if (_documents.TryGetValue(clientId, out var cached) && cached.Until > now)
        {
            return cached.Client;
        }
        var (client, error) = await FetchAsync(clientId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (error is not null)
        {
            // Both are the caller's to choose (the reason may name the URL's host): cleaned, so they can't forge log lines.
            _logger.LogWarning("Client metadata document {ClientId} refused: it {Reason}.", McpAudit.Clean(clientId), McpAudit.Clean(error));
        }
        if (_documents.Count >= MaxCached)
        {
            _documents.Clear();
        }
        _documents[clientId] = (client, now.Add(client is null ? FailureCacheFor : CacheFor));
        return client;
    }

    private async Task<(RegisteredClient? Client, string? Error)> FetchAsync(string clientId, CancellationToken cancellationToken)
    {
        var development = _environment.IsDevelopment();
        if (ClientMetadataDocument.CheckUrl(clientId, development, out var url) is { } refused)
        {
            return (null, refused);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            request.Options.Set(AllowLoopback, development && url!.Scheme == Uri.UriSchemeHttp);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (null, $"answered {(int)response.StatusCode} (redirects aren't followed)");
            }
            if (response.Content.Headers.ContentLength > ClientMetadataDocument.MaxBytes)
            {
                return (null, $"is larger than {ClientMetadataDocument.MaxBytes} bytes");
            }
            var buffer = new byte[ClientMetadataDocument.MaxBytes + 1];
            var length = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                int read;
                while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
                {
                    length += read;
                }
            }
            return ClientMetadataDocument.Parse(clientId, buffer.AsSpan(0, length), _time.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"took longer than {FetchTimeout.TotalSeconds} seconds");
        }
        catch (HttpRequestException e)
        {
            return (null, e.InnerException is RefusedAddressException refusedAddress ? refusedAddress.Message : "couldn't be fetched");
        }
    }

    /// <summary>
    /// Connects only to public addresses, checked on the socket that is actually connected: the name is resolved once,
    /// every address it gives must be public, the socket connects to exactly those, and its remote address is checked
    /// again. A DNS answer that changes between a check and the connection (DNS rebinding) can't get round it, and
    /// neither can a proxy, which is never used.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = FetchTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        MaxResponseHeadersLength = 16,
        ConnectCallback = ConnectAsync,
    };

    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var allowLoopback = context.InitialRequestMessage.Options.TryGetValue(AllowLoopback, out var allow) && allow;
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(a => !Allowed(a, allowLoopback)))
        {
            throw new RefusedAddressException(addresses.Length == 0 ? $"has a host ({host}) with no addresses" : $"resolves to a non-public address ({host})");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            if (socket.RemoteEndPoint is not IPEndPoint remote || !Allowed(remote.Address, allowLoopback))
            {
                throw new RefusedAddressException($"connected to a non-public address ({host})");
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool Allowed(IPAddress address, bool allowLoopback) =>
        NetworkAddresses.IsPublic(address) || (allowLoopback && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));

    public void Dispose() => _http.Dispose();

    /// <summary>The socket check refused the address; the message says why, without anything secret.</summary>
    internal sealed class RefusedAddressException(string message) : IOException(message);
}
