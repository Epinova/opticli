using OptiCli.Agent.Safety;
using OptiCli.Protocol;

namespace OptiCli.Agent.Hosting;

/// <summary>
/// What the CLI passed to the site process: through environment variables (<c>serve</c>, <c>env</c>), or for IDE mode
/// through runtime properties in the build's <c>runtimeconfig.json</c> (<c>opticli ide on</c>).
/// </summary>
/// <param name="ApprovedRemote">The one remote database the CLI approved (the user's development database); null for local only.</param>
/// <param name="DriftFile">What <c>serve</c> found before the site started (<see cref="StartupDrift"/>), in shared mode.</param>
/// <param name="Scheduler">
/// <c>serve --scheduler</c> (or <c>opticli env</c>): leave the site's scheduler as the site sets it. Otherwise the agent turns
/// it off. Ignored in shared mode, where it is always off.
/// </param>
/// <param name="IdeFile">
/// IDE mode (<c>opticli ide on</c>): where the agent announces itself once the site listens (<see cref="IdeRun"/>). The
/// agent then changes nothing about how the site starts, and never stops it from starting.
/// </param>
/// <param name="Events">A sealed apply's production event bus, from its stdin line only (<see cref="SealedAzure"/>).</param>
/// <param name="Blobs">A sealed apply's production blob storage, from its stdin line only.</param>
internal sealed record AgentSettings(string ConnectionName, string? PinnedConnection, string? Token, (string Server, string? Database)? ApprovedRemote = null, string? DriftFile = null, bool Scheduler = false, string? IdeFile = null,
    AzureEvents? Events = null, AzureBlobs? Blobs = null)
{
    /// <summary>The site was started from the IDE with the hook from <c>opticli ide on</c>, not by <c>serve</c> or <c>env</c>.</summary>
    public bool Ide => IdeFile is not null;

    /// <summary>The site runs against a remote, probably shared, database: nothing it does at startup may change that database for others.</summary>
    public bool SharedDatabase => ApprovedRemote is not null;

    /// <summary>The agent turns the site's scheduler off for this run (never in IDE mode: that run is the developer's).</summary>
    public bool SchedulerOff => !Ide && (SharedDatabase || !Scheduler);

    /// <summary>
    /// IDE mode when the runtimeconfig names an IDE file and the environment has no token: a token from <c>serve</c> or
    /// <c>env</c> (or a sealed apply's stdin, read into the environment before this) means that run is theirs.
    /// </summary>
    public static AgentSettings FromEnvironment() =>
        FromEnvironment(name => AppContext.GetData(name) as string);

    /// <param name="runtimeProperty">Reads a runtime property (<see cref="AppContext.GetData"/>); a parameter for tests.</param>
    internal static AgentSettings FromEnvironment(Func<string, string?> runtimeProperty)
    {
        if (NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.TokenVariable)) is null
            && NullIfEmpty(runtimeProperty(AgentProtocol.IdeFileProperty)) is { } ideFile)
        {
            return new(
                NullIfEmpty(runtimeProperty(AgentProtocol.IdeConnectionNameProperty)) ?? AgentProtocol.DefaultConnectionName,
                PinnedConnection: null,
                IdeRun.Token,
                AgentProtocol.ParseRemote(runtimeProperty(AgentProtocol.IdeRemoteDatabaseProperty)),
                Scheduler: true,
                IdeFile: ideFile);
        }
        return FromVariables();
    }

    private static AgentSettings FromVariables() => new(
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.ConnectionNameVariable)) ?? AgentProtocol.DefaultConnectionName,
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.DatabaseVariable)),
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.TokenVariable)),
        AgentProtocol.ParseRemote(Environment.GetEnvironmentVariable(AgentProtocol.RemoteDatabaseVariable)),
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.DriftFileVariable)),
        string.Equals(Environment.GetEnvironmentVariable(AgentProtocol.SchedulerVariable)?.Trim(), AgentProtocol.SchedulerOn, StringComparison.OrdinalIgnoreCase),
        Events: StdinSecrets.Given(AgentProtocol.EventsConnectionVariable) is { } events && StdinSecrets.Given(AgentProtocol.EventsTopicVariable) is { } topic ? new AzureEvents(events, topic) : null,
        Blobs: StdinSecrets.Given(AgentProtocol.BlobsConnectionVariable) is { } blobs && StdinSecrets.Given(AgentProtocol.BlobsContainerVariable) is { } container ? new AzureBlobs(blobs, container) : null);

    /// <summary>
    /// Local, or exactly the approved remote server and database (a remote without a database name would be the
    /// login's default database, so it never matches), with no failover partner other than that server.
    /// </summary>
    public bool Allows(GuardVerdict verdict) =>
        verdict.IsLocal
        || (ApprovedRemote is { Database: not null } approved
            && verdict.Server is not null
            && string.Equals(verdict.Server.Trim(), approved.Server.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(verdict.Database?.Trim(), approved.Database.Trim(), StringComparison.OrdinalIgnoreCase)
            && (verdict.Failover is null || string.Equals(verdict.Failover.Trim(), approved.Server.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
