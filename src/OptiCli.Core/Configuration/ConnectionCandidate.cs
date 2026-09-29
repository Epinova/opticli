using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OptiCli.Core.Safety;

namespace OptiCli.Core.Configuration;

/// <summary>Where a connection string was found, in resolution order.</summary>
public enum ConnectionSource
{
    Flag,
    Environment,
    UserConfig,
    UserSecrets,
    LaunchProfile,
    AppSettingsDevelopment,
    AppSettings,

    /// <summary><c>appsettings.{Environment}.json</c> for an environment other than Development: never used automatically, only when chosen.</summary>
    AppSettingsEnvironment,
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
    internal ConnectionCandidate(ConnectionSource source, string location, string? key, string? profile, string? connectionString)
    {
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
    }

    /// <summary>
    /// Short and stable while the candidate's source and target stay the same: a saved choice refers to it, and it
    /// changes when the server or database at that source does, so a changed configuration is chosen again.
    /// </summary>
    public string? Id { get; }

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

    public CandidateStatus Status { get; internal set; }

    /// <summary>Why the candidate was skipped, when it was.</summary>
    public string? Reason { get; internal set; }

    [JsonIgnore]
    internal string? ConnectionString { get; }

    [JsonIgnore]
    internal bool IsValid { get; }

    internal bool IsUsable => IsValid && Status != CandidateStatus.Unselected;

    /// <summary>Can be chosen with <c>--db</c> or <c>db use</c>: valid and from a file (not a flag or variable of this run).</summary>
    [JsonIgnore]
    public bool IsSelectable => IsValid && Source is not (ConnectionSource.Flag or ConnectionSource.Environment);

    internal bool SameTarget(string? server, string? database) =>
        string.Equals(Server?.Trim(), server?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Database?.Trim(), database?.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static ConnectionCandidate Unreadable(ConnectionSource source, string location, string reason) =>
        new(source, location, null, null, null) { Status = CandidateStatus.Invalid, Reason = reason };

    private static string? MakeId(ConnectionSource source, string location, string? key, string? profile, string? server, string? database)
    {
        if (server is null || source is ConnectionSource.Flag or ConnectionSource.Environment)
        {
            return null;
        }
        var identity = string.Join("\n", source, location, key, profile, server.Trim().ToLowerInvariant(), database?.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..6].ToLowerInvariant();
    }
}
