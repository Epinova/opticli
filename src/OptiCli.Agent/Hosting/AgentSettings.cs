using OptiCli.Agent.Safety;
using OptiCli.Protocol;

namespace OptiCli.Agent.Hosting;

/// <summary>What the CLI passed to the site process through environment variables.</summary>
/// <param name="ApprovedRemote">The one remote database the CLI approved (the user's development database); null for local only.</param>
/// <param name="DriftFile">What <c>serve</c> found before the site started (<see cref="StartupDrift"/>), in shared mode.</param>
internal sealed record AgentSettings(string ConnectionName, string? PinnedConnection, string? Token, (string Server, string? Database)? ApprovedRemote = null, string? DriftFile = null)
{
    /// <summary>The site runs against a remote, probably shared, database: nothing it does at startup may change that database for others.</summary>
    public bool SharedDatabase => ApprovedRemote is not null;

    public static AgentSettings FromEnvironment() => new(
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.ConnectionNameVariable)) ?? AgentProtocol.DefaultConnectionName,
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.DatabaseVariable)),
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.TokenVariable)),
        AgentProtocol.ParseRemote(Environment.GetEnvironmentVariable(AgentProtocol.RemoteDatabaseVariable)),
        NullIfEmpty(Environment.GetEnvironmentVariable(AgentProtocol.DriftFileVariable)));

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
