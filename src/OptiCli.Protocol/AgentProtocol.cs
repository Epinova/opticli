namespace OptiCli.Protocol;

/// <summary>
/// Constants shared by the CLI and the agent it injects into a running site.
/// </summary>
/// <remarks>
/// These files are source-linked into both sides (<c>&lt;Compile Include="..\OptiCli.Protocol\**\*.cs" /&gt;</c>)
/// rather than shipped as an assembly, so the agent stays a single DLL and the two sides can never
/// load mismatched copies. Link them into exactly one assembly per process.
/// </remarks>
public static class AgentProtocol
{
    /// <summary>
    /// Bumped on any breaking change to a route or DTO. Routes carry it as a <c>/v{N}</c> prefix, and
    /// <c>ping</c> reports it, so a CLI can tell an out-of-date agent apart from a missing one.
    /// </summary>
    public const int Version = 1;

    /// <summary>Where the agent is mapped in the site, ahead of all site middleware.</summary>
    public const string BasePath = "/_opticli";

    /// <summary>Request header carrying the per-run token.</summary>
    public const string TokenHeader = "X-OptiCli-Token";

    /// <summary>Environment variable the site process reads the per-run token from. Unset disables the agent (503).</summary>
    public const string TokenVariable = "OPTICLI_TOKEN";

    /// <summary>Environment variable with the connection string the agent pins the site to.</summary>
    public const string DatabaseVariable = "OPTICLI_DB";

    /// <summary>Optional environment variable naming the connection string to pin (default <see cref="DefaultConnectionName"/>).</summary>
    public const string ConnectionNameVariable = "OPTICLI_CONNECTION_NAME";

    public const string DefaultConnectionName = "EPiServerDB";

    /// <summary>
    /// Environment variable naming the one remote database the CLI approved (<c>server|database</c>): the user chose it
    /// as the project's development database. The agent accepts it besides local ones, and runs the site in shared
    /// mode (no scheduler, schema updates or content type sync). Unset: local databases only.
    /// </summary>
    public const string RemoteDatabaseVariable = "OPTICLI_REMOTE_DB";

    public static string FormatRemote(string server, string? database) => $"{server}|{database}";

    /// <summary>
    /// Environment variable with the path of a JSON <see cref="StartupDrift"/>: what <c>serve</c> found before the site
    /// started (EF Core migrations, the CMS schema version), for the agent's <see cref="DriftReport"/>. Shared mode only.
    /// </summary>
    public const string DriftFileVariable = "OPTICLI_DRIFT_FILE";

    /// <summary>
    /// Request header confirming a write although the build and the shared database differ: the
    /// <see cref="DriftReport.Fingerprint"/> the user agreed to. Without it (or with another one) such writes fail with
    /// <see cref="AgentErrorCodes.Drift"/>.
    /// </summary>
    public const string AcceptDriftHeader = "X-OptiCli-Accept-Drift";

    /// <returns>Null when <paramref name="value"/> is empty or malformed.</returns>
    public static (string Server, string? Database)? ParseRemote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('|') is var bar && bar <= 0)
        {
            return null;
        }
        var database = value[(bar + 1)..];
        return (value[..bar], database.Length == 0 ? null : database);
    }

    /// <summary>Identity every agent write is saved as; shows up as the version's "saved by" in the CMS.</summary>
    public const string PrincipalName = "opticli";
}
