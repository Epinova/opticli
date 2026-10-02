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

    /// <summary>The process runs (or, for <c>env</c>, may run) but the agent doesn't answer: starting, hung, or started without the agent.</summary>
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
    public static async Task<AgentStatus> ProbeAsync(StateStore store, VerifiedConnectionString? expected, CancellationToken cancellationToken)
    {
        ServeState? state;
        try
        {
            state = store.Read();
        }
        catch (CorruptStateException ex)
        {
            return new AgentStatus(AgentState.Stale, ex.Message)
            {
                StatePath = store.StatePath,
                Hint = "`opticli serve --stop` or `opticli serve --status` removes it; a site opticli started before it broke has to be stopped by hand.",
                CorruptState = true,
            };
        }
        if (state is null)
        {
            return new AgentStatus(AgentState.Stopped, "No opticli agent is running for this project.") { StatePath = store.StatePath, Hint = StartHint };
        }

        var status = Describe(state, store);
        if (state.Pid is { } pid)
        {
            using var process = SiteProcess.Find(pid, state.ProcessStartTime);
            if (process is null)
            {
                return status with
                {
                    State = AgentState.Stale,
                    Message = $"The site opticli started (pid {pid}) is no longer running.",
                    Hint = "Start it again with `opticli serve`; `opticli serve --logs` shows why it stopped.",
                };
            }
        }

        using var client = AgentClient.For(state);
        PingResponse ping;
        try
        {
            ping = await client.PingAsync(cancellationToken);
        }
        catch (OptiCliException ex) when (ex.Code == ErrorCode.Unreachable)
        {
            return status with
            {
                State = AgentState.Unresponsive,
                Message = state.Mode == ServeMode.External
                    ? $"Nothing answers on port {state.Port}: start the site with the variables from `opticli env`."
                    : $"The site (pid {state.Pid}) runs but its agent does not answer on port {state.Port}: {ex.Message}",
                Hint = state.Mode == ServeMode.External ? "Or run `opticli serve` instead." : "It may still be starting; see `opticli serve --logs`.",
            };
        }
        catch (OptiCliException ex) when (ex.Code is ErrorCode.NotFound or ErrorCode.Refused)
        {
            return status with { State = AgentState.Incompatible, Message = ex.Message, Hint = ex.Hint, Failure = ex.Code };
        }

        status = status with { Agent = AgentSummary.From(ping), Database = ping.Database };
        if (expected is not null
            && DatabaseMatch.Problem(ping.Database, expected, requirePinned: state.Mode != ServeMode.External) is { } problem)
        {
            return status with
            {
                State = AgentState.WrongDatabase,
                Message = $"The running site can't be used: {problem}.",
                Hint = "Restart it with `opticli serve --stop` and `opticli serve`.",
            };
        }
        return status with { State = AgentState.Running, Message = $"Agent running at {state.BaseUrl}." };
    }

    /// <summary>
    /// A client for the project's running agent, checked to use the database opticli reads.
    /// </summary>
    /// <exception cref="UnreachableException">No agent runs or it doesn't answer (exit 4).</exception>
    /// <exception cref="RefusedException">It uses another database (exit 3).</exception>
    public static async Task<AgentClient> ConnectAsync(StateStore store, VerifiedConnectionString expected, CancellationToken cancellationToken)
    {
        var status = await ProbeAsync(store, expected, cancellationToken);
        return status.State switch
        {
            AgentState.Running => AgentClient.For(store.Read()!),
            AgentState.WrongDatabase => throw new RefusedException(status.Message, status.Hint),
            AgentState.Incompatible when status.Failure == ErrorCode.Refused => throw new RefusedException(status.Message, status.Hint),
            AgentState.Incompatible => throw new NotFoundException(status.Message, status.Hint),
            AgentState.Stopped => throw new UnreachableException("No opticli agent is running for this project; writes go through the running site.", StartHint),
            _ => throw new UnreachableException(status.Message, status.Hint),
        };
    }

    public static AgentStatus Describe(ServeState state, StateStore store) => new(AgentState.Running, "")
    {
        Mode = state.Mode,
        Pid = state.Pid,
        Port = state.Port,
        Url = state.BaseUrl.ToString().TrimEnd('/'),
        BrowseUrl = state.HttpsPort is null ? null : state.BrowseUrl,
        StartedAt = state.StartedAt,
        LogPath = state.Mode == ServeMode.External ? null : state.LogPath,
        StatePath = store.StatePath,
        Output = state.OutputDll,
        AgentDll = state.AgentDll,
        Drift = state.Drift is { Checked: true } drift ? DriftSummary.From(drift) : null,
    };
}
