using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OptiCli.Core.Configuration;
using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;
using OptiCli.Core.Seals;
using OptiCli.Protocol;
using UnreachableException = OptiCli.Core.Errors.UnreachableException;

namespace OptiCli.Core.Serve;

/// <param name="Write">The production database and its write login: what the site runs against.</param>
/// <param name="DriftFile">What was compared before the start (EF Core migrations, schema), for the agent's drift report.</param>
/// <param name="LogPath">The site's output for this run (user-only).</param>
/// <param name="SiteEndpoints">The site's own <c>Kestrel:Endpoints</c>; opticli's address is added as one more, and every address it listens on must be loopback.</param>
public sealed record SealedLaunch(
    ProjectInfo Project,
    ProductionWrite Write,
    string ConnectionName,
    SiteOutput Output,
    string AgentDll,
    int AgentCmsMajor,
    int Port,
    TimeSpan Timeout,
    string DriftFile,
    string LogPath,
    IReadOnlyList<string>? SiteEndpoints = null);

/// <summary>
/// Starts the site against production for one sealed apply, and stops it again whatever happens. Not <c>serve</c>: no state
/// file is written (no opticli command can find the site or its token), the token and the connection string with the write
/// login go to the site on its stdin (<see cref="AgentProtocol.SecretsVariable"/>), never in its environment or on a
/// command line, the site runs in shared mode (scheduler, schema updates, content type sync and store remapping off; writes
/// stop on drift, which nothing here accepts), on <c>127.0.0.1</c> only: a site that also listens elsewhere is stopped
/// before anything is written.
/// </summary>
public static class SealedSite
{
    private const string ListeningMarker = "Now listening on:";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait, once the agent answers, for the site's log to say where it listens.</summary>
    private static readonly TimeSpan ListeningWait = TimeSpan.FromSeconds(5);

    /// <param name="started">Told the site's process id as soon as it is started (shown to the user, who can stop it by hand if opticli is killed).</param>
    /// <param name="body">Runs once the site answers, with a client for its agent.</param>
    /// <exception cref="RefusedException">The site uses another database, another CMS major, or listens on more than this machine.</exception>
    /// <exception cref="UnreachableException">The site exited or didn't answer in time.</exception>
    public static async Task<T> RunAsync<T>(SealedLaunch request, Action<int>? started, Func<AgentClient, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        var token = SiteEnvironment.NewToken();
        var listening = new ConcurrentQueue<string>();
        await using var log = OpenLog(request.LogPath);
        var gate = new object();
        using var process = SiteProcess.StartAttached(SiteProcess.DotnetHost(), request.Output.Dll, request.Project.Directory, Environment(request), redirectInput: true);
        void Copy(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (gate)
            {
                log.WriteLine(line);
            }
            var at = line.IndexOf(ListeningMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                listening.Enqueue(line[(at + ListeningMarker.Length)..].Trim());
            }
        }
        process.OutputDataReceived += (_, e) => Copy(e.Data);
        process.ErrorDataReceived += (_, e) => Copy(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // The secrets go in at once and stdin is closed: the agent's startup hook reads the one line before anything else.
        await process.StandardInput.WriteLineAsync(Secrets(request, token));
        await process.StandardInput.FlushAsync(CancellationToken.None);
        process.StandardInput.Close();
        started?.Invoke(process.Id);

        using var client = new AgentClient(new Uri(SiteEnvironment.Url(request.Port)), token);
        try
        {
            await WaitUntilReadyAsync(process, client, request, listening, () => { lock (gate) { log.Flush(); } }, cancellationToken);
            return await body(client, cancellationToken);
        }
        finally
        {
            // Ctrl+C in the terminal reaches the site too; a failure or a signal to opticli alone doesn't: either way, ask it to
            // shut down and give it time to finish what it is saving, then kill it.
            await SiteProcess.StopAsync(process, SiteLauncher.StopGrace, async () =>
            {
                try
                {
                    return (await client.ShutdownAsync(CancellationToken.None)).Stopping;
                }
                catch (OptiCliException)
                {
                    return false;
                }
            });
        }
    }

    /// <summary>
    /// The site's environment: shared mode for production, the agent as its only startup hook, and nothing secret. Inherited
    /// variables that would name another connection, token or hosting startup are removed.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string?>> Environment(SealedLaunch request)
    {
        var inherited = System.Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList();
        var removed = SiteEnvironment.CompetingConnectionVariables(request.ConnectionName, inherited)
            .Append(ConnectionVariables.Name(request.ConnectionName))
            .Concat([AgentProtocol.TokenVariable, AgentProtocol.DatabaseVariable, AgentProtocol.SchedulerVariable, AgentProtocol.ConnectionNameVariable,
                "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "ASPNETCORE_PREVENTHOSTINGSTARTUP", "ASPNETCORE_HTTPS_PORT"])
            .Distinct(StringComparer.Ordinal);
        var variables = new List<KeyValuePair<string, string?>>(removed.Select(name => new KeyValuePair<string, string?>(name, null)))
        {
            new("ASPNETCORE_ENVIRONMENT", "Development"),
            new("ASPNETCORE_URLS", SiteEnvironment.Url(request.Port)),
            // Only the agent: a hook inherited from the shell would run inside the site with production's write login.
            new(SiteEnvironment.StartupHooksVariable, request.AgentDll),
            new(AgentProtocol.SecretsVariable, AgentProtocol.SecretsFromStdin),
            new(AgentProtocol.RemoteDatabaseVariable, AgentProtocol.FormatRemote(request.Write.Server, request.Write.Database)),
            new(AgentProtocol.DriftFileVariable, request.DriftFile),
        };
        if (request.SiteEndpoints is { Count: > 0 })
        {
            variables.Add(new(KestrelEndpoints.UrlVariable(KestrelEndpoints.Http), SiteEnvironment.Url(request.Port)));
        }
        if (!string.Equals(request.ConnectionName, AgentProtocol.DefaultConnectionName, StringComparison.Ordinal))
        {
            variables.Add(new(AgentProtocol.ConnectionNameVariable, request.ConnectionName));
        }
        return variables;
    }

    /// <summary>The stdin line: the token, and the write connection as the pin and as the site's connection string.</summary>
    private static string Secrets(SealedLaunch request, string token) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [AgentProtocol.TokenVariable] = token,
            [AgentProtocol.DatabaseVariable] = request.Write.Value,
            [ConnectionVariables.Name(request.ConnectionName)] = request.Write.Value,
        });

    private static async Task WaitUntilReadyAsync(Process process, AgentClient client, SealedLaunch request, ConcurrentQueue<string> listening, Action flush, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        PingResponse? ping = null;
        while (ping is null)
        {
            if (process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None);
                flush();
                throw SiteLauncher.StartupFailure(request.LogPath, $"The site exited during startup (exit code {process.ExitCode}); nothing was written.");
            }
            if (deadline.Elapsed > request.Timeout)
            {
                flush();
                throw SiteLauncher.StartupFailure(request.LogPath, $"The site did not answer on port {request.Port} within {request.Timeout.TotalSeconds:0} s; nothing was written.", timedOut: true, port: request.Port);
            }
            try
            {
                ping = await client.PingAsync(cancellationToken);
            }
            catch (UnreachableException)
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        if (DatabaseMatch.Problem(ping.Database, request.Write.Server, request.Write.Database, request.Write.IsLocal, requirePinned: true) is { } problem)
        {
            throw new RefusedException($"Stopped the site before writing: {problem}.", "Find what in the site overrides the connection string (code or configuration that sets it after startup).");
        }
        if (SiteLauncher.AgentMismatch(ping.CmsVersion, request.AgentCmsMajor) is { } mismatch)
        {
            throw new RefusedException($"Stopped the site before writing: {mismatch}", "Build the site so its output matches the project's EPiServer packages, then seal again.");
        }
        var waited = Stopwatch.StartNew();
        while (!listening.Any(a => Loopback(a, request.Port)) && waited.Elapsed < ListeningWait)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        if (Elsewhere(listening) is { Count: > 0 } elsewhere)
        {
            throw new RefusedException(
                $"Stopped the site before writing: besides {SiteEnvironment.Url(request.Port)} it listens on {string.Join(", ", elsewhere)}, which other machines may reach while it runs against production.",
                "Something in the site sets its own addresses (Kestrel:Endpoints in its configuration, UseUrls, Listen or ConfigureKestrel in code): let it use only ASPNETCORE_URLS in Development.");
        }
    }

    /// <summary>The addresses that aren't this machine's loopback.</summary>
    internal static IReadOnlyList<string> Elsewhere(IEnumerable<string> addresses) =>
        addresses.Where(a => !Loopback(a, port: null)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <param name="port">The port it must be on; null for any.</param>
    internal static bool Loopback(string address, int? port) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
        && uri.Host.Trim('[', ']') is "127.0.0.1" or "::1" or "localhost"
        && (port is null || uri.Port == port);

    /// <summary>The run's log, readable by the user only (the site's own output; no secrets go into it from opticli).</summary>
    private static StreamWriter OpenLog(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.Read };
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new StreamWriter(new FileStream(path, options), new UTF8Encoding(false));
    }
}
