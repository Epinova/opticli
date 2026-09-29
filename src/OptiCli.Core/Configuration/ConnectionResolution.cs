using OptiCli.Core.Errors;
using OptiCli.Core.Safety;

namespace OptiCli.Core.Configuration;

/// <summary>How the connection in use was picked.</summary>
public enum SelectionMode
{
    /// <summary><c>--connection</c>, <c>OPTICLI_DB</c>, <c>--db</c> or <c>--profile</c> for this run.</summary>
    Explicit,

    /// <summary>The development database the user chose (<c>opticli db use</c>).</summary>
    Saved,

    /// <summary>The <c>connection</c> in the opticli user config.</summary>
    Configured,

    /// <summary>The local database the site's Development configuration uses.</summary>
    Automatic,
}

/// <summary>Every candidate that was considered, and either the chosen one or why none could be.</summary>
public sealed class ConnectionResolution(
    string name,
    string? projectDirectory,
    IReadOnlyList<ConnectionCandidate> candidates,
    ConnectionCandidate? chosen,
    SelectionMode? mode,
    ConnectionCandidate? development,
    SelectionMode? developmentMode,
    SavedDatabase? saved,
    OptiCliException? failure)
{
    /// <summary>Connection string name, e.g. <c>EPiServerDB</c>.</summary>
    public string Name { get; } = name;

    public IReadOnlyList<ConnectionCandidate> Candidates { get; } = candidates;

    public ConnectionCandidate? Chosen { get; } = chosen;

    public SelectionMode? Mode { get; } = mode;

    /// <summary>
    /// The project's development database: the saved choice, the configured connection, or the local database the
    /// site's Development configuration uses. Null until the user has chosen, when that is ambiguous or remote.
    /// </summary>
    public ConnectionCandidate? Development { get; } = development;

    /// <summary>How <see cref="Development"/> was found: saved, configured or automatic.</summary>
    public SelectionMode? DevelopmentMode { get; } = developmentMode;

    /// <summary>The choice saved in the user config, whether or not it still matches a candidate.</summary>
    public SavedDatabase? Saved { get; } = saved;

    public OptiCliException? Failure { get; } = failure;

    /// <summary>The candidates <c>--db</c> and <c>db use</c> accept, numbered from 1 in this order.</summary>
    public IReadOnlyList<ConnectionCandidate> Selectable => Candidates.Where(c => c.IsSelectable).ToList();

    /// <summary>A local database (always fine), or the project's development database.</summary>
    public bool IsDevelopment => Chosen is { } chosen
        && (chosen.IsLocal == true || (Development is { } development && chosen.SameTarget(development.Server, development.Database)));

    /// <summary>The chosen string, checked.</summary>
    /// <exception cref="OptiCliException">No usable candidate (the reason is in <see cref="Failure"/>).</exception>
    public VerifiedConnectionString Require() =>
        Chosen is { } chosen ? ConnectionSafety.Approve(chosen.ConnectionString) : throw Failure!;

    /// <summary>What the caller should be told about the database in use: a remote one that isn't the development database.</summary>
    public IReadOnlyList<string> Warnings()
    {
        if (Chosen is not { } chosen || IsDevelopment)
        {
            return [];
        }
        var development = Development is { } d
            ? $"'{d.Database}' on '{d.Server}'"
            : "none chosen yet, see `opticli db list`";
        return [$"Using database '{chosen.Database}' on remote server '{chosen.Server}' ({Describe(chosen)}), which is not this project's development database ({development}). Only use it when the user asked for it."];
    }

    /// <summary>The databases the user can choose from, numbered.</summary>
    public IReadOnlyList<DatabaseChoice> Choices() => DatabaseChoice.List(Candidates, projectDirectory, Development);

    /// <summary>Where a candidate came from, files inside the project relative to it.</summary>
    public string Describe(ConnectionCandidate candidate) =>
        DatabaseChoice.Describe(candidate.Source, candidate.Location, candidate.Profile, projectDirectory);

    /// <summary>The same, for a saved choice.</summary>
    public string Describe(SavedDatabase saved) =>
        DatabaseChoice.Describe(saved.Source, saved.Location, saved.Profile, projectDirectory);
}
