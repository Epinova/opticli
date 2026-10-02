using System.Net.Http.Json;
using System.Text.Json;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

/// <summary>
/// Talks to the agent inside a running site: token header, protocol-versioned routes, and error
/// envelopes mapped to the CLI's typed errors (and so to its exit codes).
/// </summary>
public sealed class AgentClient : IDisposable
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    // First writes after a start compile views and warm caches inside the CMS.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// For a write the CLI stopped waiting for: the agent doesn't save once the caller has gone, but a save already
    /// under way finishes.
    /// </summary>
    public const string MayHaveSavedHint =
        "The site may still have saved the change if it was already saving: check with `opticli versions <ref>` (or `opticli get`) before running it again.";

    private readonly HttpClient _http;

    public AgentClient(Uri baseUrl, string token, HttpMessageHandler? handler = null)
    {
        // No redirects: a site answering without the agent (HTTPS or canonical-host redirect) must not get the token sent elsewhere.
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _http.BaseAddress = new Uri(baseUrl, AgentProtocol.BasePath + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Add(AgentProtocol.TokenHeader, token);
    }

    public static AgentClient For(ServeState state) => new(state.BaseUrl, state.Token);

    /// <exception cref="UnreachableException">Nothing answers.</exception>
    /// <exception cref="OptiCliException">The agent answered with an error (e.g. out of date, wrong token).</exception>
    public Task<PingResponse> PingAsync(CancellationToken cancellationToken) =>
        SendAsync<PingResponse>(HttpMethod.Get, AgentRoutes.Ping, null, PingTimeout, cancellationToken);

    /// <summary>Asks the site to stop gracefully; it exits once running requests finish.</summary>
    /// <exception cref="UnreachableException">Nothing answers.</exception>
    /// <exception cref="OptiCliException">The agent answered with an error (an agent older than the endpoint answers not_found).</exception>
    public Task<ShutdownResponse> ShutdownAsync(CancellationToken cancellationToken) =>
        SendAsync<ShutdownResponse>(HttpMethod.Post, AgentRoutes.Shutdown, null, PingTimeout, cancellationToken);

    /// <summary>
    /// What differs between the site's code and its shared database (the first call after a start compares them, which
    /// can take a while on a large site).
    /// </summary>
    /// <exception cref="UnreachableException">Nothing answers.</exception>
    /// <exception cref="RefusedException">An agent from before drift checks, which can't say: restart the site.</exception>
    public async Task<DriftReport> DriftAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync<DriftReport>(HttpMethod.Get, AgentRoutes.Drift, null, WriteTimeout, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.Message.Contains("No agent route", StringComparison.Ordinal))
        {
            throw new RefusedException(
                "The site's agent is older than this opticli, so it can't compare this build with the shared database; writes there are refused until the site is restarted with this opticli.",
                AgentErrors.OutOfDateHint);
        }
    }

    /// <summary>Sends <paramref name="fingerprint"/> with every later request: the user confirmed writing despite those differences.</summary>
    public void AcceptDrift(string fingerprint)
    {
        _http.DefaultRequestHeaders.Remove(AgentProtocol.AcceptDriftHeader);
        _http.DefaultRequestHeaders.Add(AgentProtocol.AcceptDriftHeader, fingerprint);
    }

    public Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken cancellationToken) =>
        SendAsync<T>(method, route, body, WriteTimeout, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, route.TrimStart('/'));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: AgentJson.Options);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.SendAsync(request, timeoutSource.Token);
            text = await response.Content.ReadAsStringAsync(timeoutSource.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var timedOut = ex is OperationCanceledException;
            throw new UnreachableException(
                $"The site's opticli agent at {_http.BaseAddress} did not answer ({(timedOut ? $"no response within {timeout.TotalSeconds:0} s" : ex.Message)}).",
                timedOut && method != HttpMethod.Get
                    ? $"{MayHaveSavedHint} Check the site with `opticli serve --status` and `opticli serve --logs`."
                    : "Check it with `opticli serve --status` and `opticli serve --logs`; start it with `opticli serve`.",
                ex);
        }

        using (response)
        {
            return Parse<T>(text, (int)response.StatusCode);
        }
    }

    /// <summary>Unwraps an agent envelope; exposed for tests.</summary>
    public static T Parse<T>(string text, int status)
    {
        AgentResponse<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AgentResponse<T>>(text, AgentJson.Options);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope?.Meta is null && TooLarge(text, status))
        {
            throw new UsageException(
                $"The site's web server refused the request as too large (HTTP {status}{(text.Contains("404.13", StringComparison.Ordinal) ? ", IIS request filtering 404.13" : "")}) before the agent saw it.",
                "Upload a smaller file, or raise the site's limit (IIS: requestLimits maxAllowedContentLength in web.config; Kestrel: MaxRequestBodySize) for local development.");
        }
        if (envelope?.Meta is null)
        {
            // Something other than the agent answered: the site's own error page, a proxy, another app on the port.
            throw new AgentMissingException(
                $"The site answered HTTP {status} without an opticli envelope; the agent is not loaded or another program uses the port.",
                "Check `opticli serve --status` and `opticli serve --logs`.");
        }
        if (!envelope!.Ok || envelope.Error is not null)
        {
            // An agent older than this CLI rejects request fields it doesn't know.
            if (envelope.Error is { Code: AgentErrorCodes.Usage } unknown && unknown.Message.Contains("could not be mapped", StringComparison.Ordinal))
            {
                throw new UsageException(
                    $"The site's agent ({envelope.Meta.Version}) doesn't know a field this opticli sends: {unknown.Message}",
                    AgentErrors.OutOfDateHint);
            }
            throw AgentErrors.ToException(envelope.Error ?? new AgentError(AgentErrorCodes.Internal, $"HTTP {status} without error details."));
        }
        if (envelope.Meta.Protocol != AgentProtocol.Version)
        {
            throw new NotFoundException(
                $"The site's agent speaks opticli protocol v{envelope.Meta.Protocol}, this CLI v{AgentProtocol.Version}: the agent is out of date.",
                AgentErrors.OutOfDateHint);
        }
        return envelope.Data ?? throw new InternalException("The agent returned no data.");
    }

    /// <summary>A server's own answer to a body over its limit: 413, or IIS request filtering's 404.13 page.</summary>
    internal static bool TooLarge(string text, int status) =>
        status == 413 || (status == 404 && (text.Contains("404.13", StringComparison.Ordinal) || text.Contains("maxAllowedContentLength", StringComparison.Ordinal)));

    public void Dispose() => _http.Dispose();
}

/// <summary>Something answers on the agent's port, but not the agent (exit 4 like any unreachable agent).</summary>
public sealed class AgentMissingException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Unreachable, message, hint);
