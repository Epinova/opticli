using System.Diagnostics;
using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;
using OptiCli.Protocol;
using UnreachableException = OptiCli.Core.Errors.UnreachableException;

namespace OptiCli.Core.Serve;

/// <param name="ConnectionName">Name of the connection string the agent pins (<c>EPiServerDB</c>).</param>
/// <param name="Timeout">How long to wait for the agent to answer after starting the process.</param>
public sealed record LaunchRequest(
    ProjectInfo Project,
    VerifiedConnectionString Connection,
    string ConnectionName,
    SiteOutput Output,
    string AgentDll,
    int Port,
    TimeSpan Timeout,
    int? HttpsPort = null);

/// <summary>Starts the site with the agent injected, waits until the agent answers and checks it uses the pinned database.</summary>
public static class SiteLauncher
{
    /// <summary>Log lines included in a startup failure.</summary>
    public const int FailureLogLines = 40;

    /// <summary>What the agent's hosting startup writes when it refuses an unapproved or overridden database.</summary>
    public const string RefusalMarker = "[opticli] Refusing to start";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(20);

    /// <summary>Starts the site detached from this process; returns once the agent answers.</summary>
    public static async Task<AgentStatus> StartBackgroundAsync(StateStore store, LaunchRequest request, CancellationToken cancellationToken)
    {
        store.PrepareLog();
        var token = SiteEnvironment.NewToken();
        using var process = SiteProcess.StartDetached(SiteProcess.DotnetHost(), request.Output.Dll, request.Project.Directory, Environment(request, token), store.LogPath);
        var state = Record(store, request, token, ServeMode.Background, process);
        await WaitUntilReadyAsync(store, state, process, request, cancellationToken);
        return await AgentProbe.ProbeAsync(store, request.Connection, cancellationToken);
    }

    /// <summary>Runs the site attached, copying its output to <paramref name="output"/> and the log, until it exits or is cancelled (Ctrl+C).</summary>
    /// <param name="onReady">Called once the agent answers.</param>
    public static async Task RunForegroundAsync(StateStore store, LaunchRequest request, TextWriter output, Action<AgentStatus> onReady, CancellationToken cancellationToken)
    {
        var token = SiteEnvironment.NewToken();
        await using var log = new StreamWriter(store.CreateLog()) { AutoFlush = true };
        using var process = SiteProcess.StartAttached(SiteProcess.DotnetHost(), request.Output.Dll, request.Project.Directory, Environment(request, token));
        var gate = new object();
        void Copy(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (gate)
            {
                output.WriteLine(line);
                log.WriteLine(line);
            }
        }
        process.OutputDataReceived += (_, e) => Copy(e.Data);
        process.ErrorDataReceived += (_, e) => Copy(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var state = Record(store, request, token, ServeMode.Foreground, process);
        try
        {
            await WaitUntilReadyAsync(store, state, process, request, cancellationToken);
            onReady(await AgentProbe.ProbeAsync(store, request.Connection, cancellationToken));
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new UnreachableException($"The site exited with code {process.ExitCode}.", $"Its output is above and in {store.LogPath}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C reaches the site too; give it time to shut down cleanly.
            await SiteProcess.StopAsync(process, StopGrace);
        }
        finally
        {
            store.Delete();
        }
    }

    /// <summary>Stops the site opticli started for this project and removes its state file.</summary>
    /// <returns>Null when nothing was running.</returns>
    public static async Task<StopResult?> StopAsync(StateStore store)
    {
        var state = store.Read();
        if (state is null)
        {
            return null;
        }
        var result = new StopResult(state.Mode, state.Pid, state.Port, Stopped: false, Graceful: false);
        if (state.Pid is { } pid && SiteProcess.Find(pid, state.ProcessStartTime) is { } process)
        {
            using (process)
            {
                var graceful = await SiteProcess.StopAsync(process, StopGrace);
                result = result with { Stopped = true, Graceful = graceful };
            }
        }
        store.Delete();
        return result;
    }

    private static IReadOnlyList<KeyValuePair<string, string?>> Environment(LaunchRequest request, string token)
    {
        var variables = SiteEnvironment.Build(
            request.AgentDll,
            token,
            request.Port,
            request.ConnectionName,
            request.Connection,
            System.Environment.GetEnvironmentVariable(SiteEnvironment.StartupHooksVariable),
            approvedRemote: request.Connection,
            httpsPort: request.HttpsPort);
        // The site inherits this process's environment. Other spellings of the pinned ConnectionStrings variable go first
        // (removed, not blanked: an empty one would still be read). Then blank what this run doesn't set, so a stale export
        // from `opticli env` can't approve another remote database or pin another connection name.
        var competing = SiteEnvironment.CompetingConnectionVariables(request.ConnectionName, System.Environment.GetEnvironmentVariables().Keys.OfType<string>());
        string[] owned = [AgentProtocol.TokenVariable, AgentProtocol.DatabaseVariable, AgentProtocol.ConnectionNameVariable, AgentProtocol.RemoteDatabaseVariable];
        return
        [
            .. competing.Select(name => new KeyValuePair<string, string?>(name, null)),
            .. variables.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)),
            .. owned.Where(name => variables.All(v => v.Key != name)).Select(name => new KeyValuePair<string, string?>(name, "")),
        ];
    }

    private static ServeState Record(StateStore store, LaunchRequest request, string token, ServeMode mode, Process process)
    {
        var state = new ServeState(
            mode,
            store.ProjectDirectory,
            request.Port,
            token,
            DateTimeOffset.UtcNow,
            store.LogPath,
            request.Connection.Server,
            request.Connection.Database,
            process.Id,
            SiteProcess.StartTime(process),
            request.Output.Dll,
            request.AgentDll,
            request.HttpsPort);
        store.Write(state);
        return state;
    }

    /// <exception cref="RefusedException">The agent refused the database, or the site reports another one (the site is stopped).</exception>
    /// <exception cref="UnreachableException">The site exited or didn't answer in time (the site is stopped).</exception>
    private static async Task WaitUntilReadyAsync(StateStore store, ServeState state, Process process, LaunchRequest request, CancellationToken cancellationToken)
    {
        using var client = AgentClient.For(state);
        var deadline = Stopwatch.StartNew();
        PingResponse? ping = null;
        try
        {
            while (ping is null)
            {
                if (process.HasExited)
                {
                    throw StartupFailure(store, $"The site exited during startup (exit code {process.ExitCode}).");
                }
                if (deadline.Elapsed > request.Timeout)
                {
                    throw StartupFailure(store, $"The site did not answer on port {request.Port} within {request.Timeout.TotalSeconds:0} s.", timedOut: true);
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

            if (DatabaseMatch.Problem(ping.Database, request.Connection, requirePinned: true) is { } problem)
            {
                throw new RefusedException($"Stopped the site: {problem}.", "Find what in the site overrides the connection string (code or configuration that sets it after startup).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SiteProcess.StopAsync(process, StopGrace);
            store.Delete();
            throw;
        }
    }

    internal static OptiCliException StartupFailure(StateStore store, string message, bool timedOut = false)
    {
        var lines = LogTail.Read(store.LogPath, FailureLogLines);
        var details = new StartupLog(store.LogPath, lines);
        if (lines.Any(l => l.Contains(RefusalMarker, StringComparison.Ordinal)))
        {
            return new RefusedException($"{message} The opticli agent refused to start the site: {lines.Last(l => l.Contains(RefusalMarker, StringComparison.Ordinal)).Trim()}",
                "The site's effective database is not the one opticli pinned; details.lines has the site's output.") { Details = details };
        }
        if (lines.Any(l => l.Contains("Unable to configure HTTPS endpoint", StringComparison.Ordinal) || l.Contains("developer certificate could not be found", StringComparison.Ordinal)))
        {
            return new UnreachableException($"{message} The site found no HTTPS development certificate.",
                "Create one with `dotnet dev-certs https --trust`, in the same environment the site runs in, or start without --https.")
            { Details = details };
        }
        return new UnreachableException(message,
            timedOut ? "Raise --timeout if the site is just slow; details.lines has its latest output." : "details.lines has the site's last output; fix the error and start again.")
        { Details = details };
    }
}

public sealed record StopResult(ServeMode Mode, int? Pid, int? Port, bool Stopped, bool Graceful);

public sealed record StartupLog(string Log, IReadOnlyList<string> Lines);
