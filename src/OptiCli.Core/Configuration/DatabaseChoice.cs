namespace OptiCli.Core.Configuration;

/// <summary>
/// One database the user can choose, as listed by <c>db list</c> and in a <c>needs_selection</c> error.
/// <see cref="N"/> is for people to refer to; commands take <see cref="Id"/>, which stops working when the
/// configuration behind it changes.
/// </summary>
/// <param name="From">Where the connection string was found (files in the project relative to it).</param>
/// <param name="Development">True for the project's development database (chosen, configured, or the local default); omitted otherwise.</param>
/// <param name="Name">The name it was added with (<c>opticli db add</c>), which <c>--db</c> takes too.</param>
/// <param name="Role">The role of its server and database: <c>local</c>, <c>dev</c>, <c>test</c> or <c>prod</c>; omitted for a remote one without a role.</param>
public sealed record DatabaseChoice(int N, string Id, string? Server, string? Database, bool Local, string From, bool? Development = null, string? Name = null, DatabaseRole? Role = null)
{
    public static IReadOnlyList<DatabaseChoice> List(IEnumerable<ConnectionCandidate> candidates, string? projectDirectory, ConnectionCandidate? development) =>
        candidates.Where(c => c.IsSelectable)
            .Select((c, i) => new DatabaseChoice(i + 1, c.Id!, c.Server, c.Database, c.IsLocal == true, Describe(c.Source, c.Location, c.Profile, projectDirectory),
                ReferenceEquals(c, development) ? true : null, c.Name, c.Role))
            .ToList();

    /// <summary>A short human description of a candidate's origin.</summary>
    public static string Describe(ConnectionSource source, string location, string? profile, string? projectDirectory)
    {
        var where = source switch
        {
            ConnectionSource.UserSecrets => "user secrets",
            ConnectionSource.UserConfig => "opticli user config",
            ConnectionSource.Named => "opticli user config (db add)",
            ConnectionSource.Environment => $"environment ({location})",
            _ when projectDirectory is not null && location.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                => Path.GetRelativePath(projectDirectory, location).Replace('\\', '/'),
            _ => location,
        };
        return profile is null ? where : $"{where} [{profile}]";
    }
}
