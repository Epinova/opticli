namespace OptiCli.Core.Configuration;

/// <summary>The roles given in the user config, by server and database (<see cref="DatabaseKey"/>).</summary>
public sealed class RoleTable(IReadOnlyList<RoleEntry> entries)
{
    public IReadOnlyList<RoleEntry> Entries { get; } = entries;

    /// <summary>The role given to this server and database; null when none was.</summary>
    public RoleEntry? Assigned(DatabaseKey? key) =>
        key is null ? null : Entries.LastOrDefault(e => e.Key == key);

    /// <summary>The role given to it, else <c>local</c> for a local database; null for a remote one without a role.</summary>
    public DatabaseRole? Of(string? server, string? database, bool? isLocal) =>
        Assigned(DatabaseKey.Of(server, database))?.Role ?? (isLocal == true ? DatabaseRole.Local : null);

    internal DatabaseRole? Of(ConnectionCandidate candidate) =>
        candidate.Target is { } key ? Assigned(key)?.Role ?? (candidate.IsLocal == true ? DatabaseRole.Local : null) : null;
}
