using OptiCli.Core.Errors;

namespace OptiCli.Core.Configuration;

/// <summary>
/// What a database is for, from the weakest to the strongest. A database on this machine is <see cref="Local"/> unless
/// it was given a role; a remote one has none until it is given one. <see cref="Prod"/> is only ever read: never the
/// development database, never written to, never served.
/// </summary>
public enum DatabaseRole
{
    Local,
    Dev,
    Test,
    Prod,
}

public static class DatabaseRoles
{
    /// <summary>The roles as <c>db add --role</c> and <c>db role</c> take them.</summary>
    public const string Syntax = "local, dev, test or prod";

    public static string Name(DatabaseRole role) => role.ToString().ToLowerInvariant();

    /// <exception cref="UsageException">Not one of the roles.</exception>
    public static DatabaseRole Parse(string text, string what) =>
        TryParse(text) ?? throw new UsageException($"{what} '{text}' is not a role: use {Syntax}.");

    public static DatabaseRole? TryParse(string? text) =>
        text?.Trim().ToLowerInvariant() switch
        {
            "local" => DatabaseRole.Local,
            "dev" => DatabaseRole.Dev,
            "test" => DatabaseRole.Test,
            "prod" => DatabaseRole.Prod,
            _ => null,
        };

    /// <summary>
    /// True when giving a database <paramref name="wanted"/> weakens the role it was given: that needs a person at a
    /// terminal, so an agent can't make production writable (by adding a second entry for it, say).
    /// </summary>
    /// <param name="assigned">The role the database was given; null when it has none (the default).</param>
    public static bool Lowers(DatabaseRole? assigned, DatabaseRole wanted) => assigned is { } current && wanted < current;
}
