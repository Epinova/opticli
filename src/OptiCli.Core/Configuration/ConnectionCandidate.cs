using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OptiCli.Core.Safety;

namespace OptiCli.Core.Configuration;

/// <summary>Where a connection string was found, in resolution order.</summary>
public enum ConnectionSource
{
    Flag,

    /// <summary><c>OPTICLI_DB</c>: like <see cref="Flag"/>, for this run.</summary>
    OptiCliDb,

    UserConfig,
    LaunchProfile,

    /// <summary>An exported <c>ConnectionStrings__&lt;Name&gt;</c> (or <c>ConnectionStrings:&lt;Name&gt;</c>), which the site reads too.</summary>
    Environment,

    UserSecrets,
    AppSettingsDevelopment,
    AppSettings,

    /// <summary><c>appsettings.{Environment}.json</c> for an environment other than Development: never used automatically, only when chosen.</summary>
    AppSettingsEnvironment,

    /// <summary>A database added by name with <c>opticli db add</c> (in the opticli user config): only used when chosen.</summary>
    Named,
}

public enum CandidateStatus
{
    /// <summary>The one opticli uses.</summary>
    Chosen,

    /// <summary>Valid and part of the site's Development configuration, but another candidate is used.</summary>
    Shadowed,

    /// <summary>Valid, from another environment's settings: used only when chosen.</summary>
    Available,

    /// <summary>Present but empty.</summary>
    Empty,

    /// <summary>Unparseable connection string or unreadable file.</summary>
    Invalid,

    /// <summary>One of several launch profiles pointing at different databases.</summary>
    Ambiguous,

    /// <summary>A launch profile other than the one passed with <c>--profile</c>.</summary>
    Unselected,
}

/// <summary>
/// One place a connection string was found. The string itself is internal and never serialised;
/// only the server and database names are exposed.
/// </summary>
public sealed class ConnectionCandidate
{
    internal ConnectionCandidate(ConnectionSource source, string location, string? key, string? profile, string? connectionString, string? name = null)
    {
        Name = name;
        Source = source;
        Location = location;
        Key = key;
        Profile = profile;
        ConnectionString = connectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Status = CandidateStatus.Empty;
            return;
        }

        var verdict = ConnectionSafety.Check(connectionString);
        Server = verdict.Server;
        Database = verdict.Database;
        IsValid = verdict.IsValid && !string.IsNullOrWhiteSpace(verdict.Server);
        IsLocal = IsValid ? verdict.IsLocal : null;
        Reason = verdict.IsValid ? (IsValid ? null : "The connection string names no server.") : verdict.Reason;
        Status = IsValid ? CandidateStatus.Shadowed : CandidateStatus.Invalid;
        Id = MakeId(source, location, key, profile, Server, Database);
        LegacyId = MakeLegacyId(source, location, key, profile, Server, Database);
    }

    /// <summary>
    /// Short and stable while the candidate's source and target stay the same: a saved choice refers to it, and it
    /// changes when the server or database at that source does, so a changed configuration is chosen again. The same
    /// in every checkout of the project (a git worktree): it names a file by its name, not its path.
    /// </summary>
    public string? Id { get; }

    /// <summary>The id up to opticli 0.16, made from the file's full path: a choice saved then still matches.</summary>
    [JsonIgnore]
    internal string? LegacyId { get; }

    /// <summary>The name it was added with (<see cref="ConnectionSource.Named"/>), which <c>--db</c> takes.</summary>
    public string? Name { get; }

    public ConnectionSource Source { get; }

    /// <summary>File path, or the flag / environment variable name.</summary>
    public string Location { get; }

    /// <summary>The configuration key the value was read from.</summary>
    public string? Key { get; }

    /// <summary>Launch profile name, for <see cref="ConnectionSource.LaunchProfile"/>.</summary>
    public string? Profile { get; }

    public string? Server { get; }

    public string? Database { get; }

    /// <summary>On this machine; null when the string is empty or invalid.</summary>
    public bool? IsLocal { get; }

    /// <summary>
    /// The role of its server and database (<see cref="DatabaseKey"/>): the one given to them, else <c>local</c> for a
    /// local database; null for a remote one that has none, or an invalid string.
    /// </summary>
    public DatabaseRole? Role { get; internal set; }

    /// <summary>Its server and database as roles are kept; null for an empty or invalid string.</summary>
    [JsonIgnore]
    public DatabaseKey? Target => IsValid ? DatabaseKey.Of(Server, Database) : null;

    public CandidateStatus Status { get; internal set; }

    /// <summary>Why the candidate was skipped, when it was.</summary>
    public string? Reason { get; internal set; }

    [JsonIgnore]
    internal string? ConnectionString { get; }

    [JsonIgnore]
    internal bool IsValid { get; }

    internal bool IsUsable => IsValid && Status != CandidateStatus.Unselected;

    /// <summary>Can be chosen with <c>--db</c> or <c>db use</c>: valid and part of the site's configuration (not <c>--connection</c> or <c>OPTICLI_DB</c>).</summary>
    [JsonIgnore]
    public bool IsSelectable => IsValid && Source is not (ConnectionSource.Flag or ConnectionSource.OptiCliDb);

    internal bool SameTarget(string? server, string? database) =>
        string.Equals(Server?.Trim(), server?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Database?.Trim(), database?.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static ConnectionCandidate Unreadable(ConnectionSource source, string location, string reason) =>
        new(source, location, null, null, null) { Status = CandidateStatus.Invalid, Reason = reason };

    private static string? MakeId(ConnectionSource source, string location, string? key, string? profile, string? server, string? database) =>
        // A file is one of the project's own (appsettings, launchSettings), its user secrets or the opticli user config,
        // so its name tells it apart; the user config's key holds the project's path.
        Hash(source, source is ConnectionSource.Environment ? location : Path.GetFileName(location),
            source is ConnectionSource.UserConfig ? null : key, profile, server, database);

    private static string? MakeLegacyId(ConnectionSource source, string location, string? key, string? profile, string? server, string? database) =>
        Hash(source, location, key, profile, server, database);

    private static string? Hash(ConnectionSource source, string location, string? key, string? profile, string? server, string? database)
    {
        if (server is null || source is ConnectionSource.Flag or ConnectionSource.OptiCliDb)
        {
            return null;
        }
        var identity = string.Join("\n", source, location, key, profile, server.Trim().ToLowerInvariant(), database?.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..6].ToLowerInvariant();
    }
}
