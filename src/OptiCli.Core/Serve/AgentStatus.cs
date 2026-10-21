using System.Text.Json.Serialization;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

public enum AgentState
{
    /// <summary>The site answers ping and uses the expected database.</summary>
    Running,

    /// <summary>No state file: nothing was started for this project.</summary>
    Stopped,

    /// <summary>A state file whose process is gone (crashed, killed, machine restarted).</summary>
    Stale,

    /// <summary>The process runs (or, for <c>env</c>, may run) but the agent doesn't answer: starting, hung, paused in the debugger, or started without the agent.</summary>
    Unresponsive,

    /// <summary>The agent answers but speaks another protocol version or rejects the token.</summary>
    Incompatible,

    /// <summary>The agent answers but its CMS uses another database than opticli reads.</summary>
    WrongDatabase,
}

/// <summary>What <c>serve --status</c> and <c>doctor</c> report about a project's agent.</summary>
public sealed record AgentStatus(AgentState State, string Message)
{
    public ServeMode? Mode { get; init; }

    public int? Pid { get; init; }

    public int? Port { get; init; }

    public string? Url { get; init; }

    /// <summary>Where to open the site in a browser, when it isn't <see cref="Url"/> (<c>serve --https</c>).</summary>
    public string? BrowseUrl { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public string? LogPath { get; init; }

    public string? StatePath { get; init; }

    public string? Output { get; init; }

    public string? AgentDll { get; init; }

    public AgentSummary? Agent { get; init; }

    public DatabaseTarget? Database { get; init; }

    /// <summary>Against a shared database: how the site's code differs from it (<c>opticli drift</c> has the list).</summary>
    public DriftSummary? Drift { get; init; }

    /// <summary>
    /// <c>on</c> or <c>off</c>: whether the site's scheduler starts jobs on their schedule, as the running site reports it.
    /// Null when no site answers (or its agent is older than the field).
    /// </summary>
    public string? Scheduler { get; init; }

    public string? Hint { get; init; }

    /// <summary>For <see cref="AgentState.Stale"/>: the state file itself is unreadable, so nothing is known about the site.</summary>
    [JsonIgnore] public bool CorruptState { get; init; }

    /// <summary>For <see cref="AgentState.Incompatible"/>: the error the agent answered with.</summary>
    [JsonIgnore] public ErrorCode? Failure { get; init; }
}

/// <summary>The drift report in short.</summary>
/// <param name="Ahead">One of <see cref="DriftAhead"/>; null when nothing differs.</param>
public sealed record DriftSummary(int Differences, string? Ahead)
{
    public static DriftSummary From(DriftReport report) => new(report.Differences, report.Ahead);
}

/// <summary>The ping fields worth showing.</summary>
public sealed record AgentSummary(string Version, int Protocol, string CmsVersion, string Runtime, string Environment, string Principal)
{
    public static AgentSummary From(PingResponse ping) =>
        new(ping.AgentVersion, ping.Protocol, ping.CmsVersion, ping.Runtime, ping.Environment, ping.Principal);
}

/// <summary>Reads a project's state file and checks the process and agent behind it.</summary>
public static class AgentProbe
{
    public const string StartHint = "Start it with `opticli serve`.";

    /// <param name="expected">The database opticli reads; when given, a site on another database is reported as <see cref="AgentState.WrongDatabase"/>.</param>
    /// <remarks>An unreadable state file is <see cref="AgentState.Stale"/> with <see cref="AgentStatus.CorruptState"/> set; it is left in place.</remarks>
    public static async Task<AgentStatus> ProbeAsync(StateStore store, VerifiedConnectionString? expected, CancellationToken cancellationToken) =>
        (await ProbeSiteAsync(store, expected, cancellationToken)).Status;

    /// <summary>
    /// <see cref="ProbeAsync"/>, with the state it probed: a site started from the IDE (<c>opticli ide on</c>) that still
    /// runs wins over the state file, since its cache is the one the developer looks at.
    /// </summary>
    private static async Task<(AgentStatus Status, ServeState? State)> ProbeSiteAsync(StateStore store, VerifiedConnectionString? expected, CancellationToken cancellationToken)
    {
        if (IdeSite.Running(store) is { } ide)
        {
            return (await ProbeIdeAsync(store, ide, expected, cancellationToken), ide);
        }
        return await ProbeStateFileAsync(store, expected, cancellationToken);
    }

    private static async Task<AgentStatus> ProbeIdeAsync(StateStore store, ServeState ide, VerifiedConnectionString? expected, CancellationToken cancellationToken)
    {
        var status = Describe(ide, store);
        if (ide.AgentUrl is null)
        {
            return status with
            {
                State = AgentState.Unresponsive,
                Url = null,
                Message = $"The site your IDE runs (pid {ide.Pid}) listens on no address of this machine opticli can use (see {store.IdePath}).",
                Hint = "Give its launch profile a localhost address (applicationUrl), or use `opticli serve`.",
            };
        }
        var (probed, ping) = await PingAsync(ide, status, cancellationToken);
        if (ping is null)
        {
            return probed;
        }
        probed = probed with { Hint = OtherSiteHint(store) };
        if (expected is not null && DatabaseMatch.Problem(ping.Database, expected, requirePinned: false) is { } problem)
        {
            return probed with
            {
                State = AgentState.WrongDatabase,
                Message = $"The site your IDE runs can't be used: {problem}.",
                Hint = "Point the site's connection string at the database opticli reads (`opticli doctor`), and restart it from the IDE.",
            };
        }
        return probed with { State = AgentState.Running, Message = $"Agent running in the site your IDE started (pid {ide.Pid}), at {ide.BaseUrl}." };
    }

    /// <summary>A site <c>serve</c> started still runs beside the IDE's, with its own cache: worth stopping.</summary>
    private static string? OtherSiteHint(StateStore store)
    {
        try
        {
            if (store.Read() is { Pid: { } pid } served && served.Mode != ServeMode.Ide)
            {
                using var process = SiteProcess.Find(pid, served.ProcessStartTime);
                if (process is not null)
                {
                    return $"`opticli serve` also runs this site (pid {pid}), which keeps its own cache: stop it with `opticli serve --stop`.";
                }
            }
        }
        catch (CorruptStateException)
        {
        }
        return null;
    }

    /// <summary>Pings the agent; on failure the status says why and the ping is null.</summary>
    private static async Task<(AgentStatus Status, PingResponse? Ping)> PingAsync(ServeState state, AgentStatus status, CancellationToken cancellationToken)
    {
        using var client = AgentClient.For(state);
        PingResponse ping;
        try
        {
            ping = await client.PingAsync(cancellationToken);
        }
        catch (OptiCliException ex) when (ex.Code == ErrorCode.Unreachable)
        {
            return (status with
            {
                State = AgentState.Unresponsive,
                Message = state.Mode switch
                {
                    ServeMode.External => $"Nothing answers on port {state.Port}: start the site with the variables from `opticli env`.",
                    ServeMode.Ide => $"The site your IDE runs (pid {state.Pid}) doesn't answer at {state.BaseUrl}: {ex.Message}",
                    _ => $"The site (pid {state.Pid}) runs but its agent does not answer on port {state.Port}: {ex.Message}",
                },
                Hint = state.Mode switch
                {
                    ServeMode.External => "Or run `opticli serve` instead.",
                    ServeMode.Ide => "It may be paused in the debugger, or still starting: continue it, then try again.",
                    _ => "It may still be starting; see `opticli serve --logs`.",
                },
            }, null);
        }
        catch (OptiCliException ex) when (ex.Code is ErrorCode.NotFound or ErrorCode.Refused)
        {
            return (status with
            {
                State = AgentState.Incompatible,
                Message = ex.Message,
                Hint = state.Mode == ServeMode.Ide && ex.Message.Contains("out of date", StringComparison.Ordinal)
                    ? "Run `opticli ide on` again, rebuild, and restart the site from the IDE."
                    : ex.Hint,
                Failure = ex.Code,
            }, null);
        }
        return (status with { Agent = AgentSummary.From(ping), Database = ping.Database, Scheduler = SchedulerText(ping.Scheduler) }, ping);
    }

    private static async Task<(AgentStatus Status, ServeState? State)> ProbeStateFileAsync(StateStore store, VerifiedConnectionString? expected, CancellationToken cancellationToken)
    {
        ServeState? state;
        try
        {
            state = store.Read();
        }
        catch (CorruptStateException ex)
        {
            return (new AgentStatus(AgentState.Stale, ex.Message)
            {
                StatePath = store.StatePath,
                Hint = "`opticli serve --stop` or `opticli serve --status` removes it; a site opticli started before it broke has to be stopped by hand.",
                CorruptState = true,
            }, null);
        }
        if (state is null)
        {
            return (new AgentStatus(AgentState.Stopped, "No opticli agent is running for this project.") { StatePath = store.StatePath, Hint = StartHintFor(store) }, null);
        }

        var status = Describe(state, store);
        if (state.Pid is { } pid)
        {
            using var process = SiteProcess.Find(pid, state.ProcessStartTime);
            if (process is null)
            {
                return (status with
                {
                    State = AgentState.Stale,
                    Message = $"The site opticli started (pid {pid}) is no longer running.",
                    Hint = "Start it again with `opticli serve`; `opticli serve --logs` shows why it stopped.",
                }, state);
            }
        }

        var (probed, ping) = await PingAsync(state, status, cancellationToken);
        if (ping is null)
        {
            return (probed, state);
        }
        if (expected is not null
            && DatabaseMatch.Problem(ping.Database, expected, requirePinned: state.Mode != ServeMode.External) is { } problem)
        {
            return (probed with
            {
                State = AgentState.WrongDatabase,
                Message = $"The running site can't be used: {problem}.",
                Hint = "Restart it with `opticli serve --stop` and `opticli serve`.",
            }, state);
        }
        return (probed with { State = AgentState.Running, Message = $"Agent running at {state.BaseUrl}." }, state);
    }

    /// <summary>
    /// A client for the project's running agent, checked to use the database opticli reads.
    /// </summary>
    /// <exception cref="UnreachableException">No agent runs or it doesn't answer (exit 4).</exception>
    /// <exception cref="RefusedException">It uses another database, or the database is production (exit 3).</exception>
    public static async Task<AgentClient> ConnectAsync(StateStore store, VerifiedConnectionString expected, CancellationToken cancellationToken)
    {
        // Everything the agent is asked for runs the site's code against its database: never production.
        ProductionDatabase.RequireNot(expected, "use the site's agent against");
        var (status, state) = await ProbeSiteAsync(store, expected, cancellationToken);
        return status.State switch
        {
            AgentState.Running => AgentClient.For(state!),
            AgentState.WrongDatabase => throw new RefusedException(status.Message, status.Hint),
            AgentState.Incompatible when status.Failure == ErrorCode.Refused => throw new RefusedException(status.Message, status.Hint),
            AgentState.Incompatible => throw new NotFoundException(status.Message, status.Hint),
            AgentState.Stopped => throw new UnreachableException("No opticli agent is running for this project; writes go through the running site.", status.Hint ?? StartHint),
            _ => throw new UnreachableException(status.Message, status.Hint),
        };
    }

    /// <summary>With IDE mode on, the site the developer starts from the IDE is the one to write through.</summary>
    private static string StartHintFor(StateStore store) =>
        IdeProject.IsOn(store.ProjectDirectory)
            ? "IDE mode is on: start the site from the IDE (opticli finds it), or with `opticli serve`. If it already runs, it was built without the agent: Rebuild in the IDE (Run's up-to-date check can skip it) and start it again; `opticli ide status` shows the build."
            : StartHint;

    public static string? SchedulerText(bool? enabled) => enabled switch { true => "on", false => "off", null => null };

    public static AgentStatus Describe(ServeState state, StateStore store) => new(AgentState.Running, "")
    {
        Mode = state.Mode,
        Pid = state.Pid,
        Port = state.Port,
        Url = state.BaseUrl.ToString().TrimEnd('/'),
        BrowseUrl = state.HttpsPort is null ? null : state.BrowseUrl,
        StartedAt = state.StartedAt,
        LogPath = state.Mode is ServeMode.External or ServeMode.Ide ? null : state.LogPath,
        StatePath = store.StatePath,
        Output = state.OutputDll,
        AgentDll = state.AgentDll,
        Drift = state.Drift is { Checked: true } drift ? DriftSummary.From(drift) : null,
    };
}
