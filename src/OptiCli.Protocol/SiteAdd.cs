namespace OptiCli.Protocol;

/// <summary>
/// Body of <see cref="AgentRoutes.SiteAdd"/>: a new site definition (CMS 13: an in-process application), for a database
/// built from scratch, which has none, so nothing renders. With <see cref="UpdateExisting"/>, a site of that name is made to
/// match instead: its start page set and the hosts added, as a plan run again does.
/// </summary>
public sealed record SiteAddRequest
{
    /// <summary>The site's name (CMS 13: its display name; the application name is made from it).</summary>
    public required string Name { get; init; }

    /// <summary>The start page's content id (CMS 13: the application's entry point).</summary>
    public required int StartPage { get; init; }

    /// <summary>
    /// The hosts, in order. In each language (and among those for every language), the first host without a type is the
    /// primary one when none has the type primary, and the rest are undefined.
    /// </summary>
    public required IReadOnlyList<SiteAddHost> Hosts { get; init; }

    /// <summary>The https setting of hosts whose own text has no scheme (<c>--https</c>); null: none given.</summary>
    public bool? Https { get; init; }

    /// <summary>A site of that name is updated to match (start page, hosts added) instead of being a <c>conflict</c>.</summary>
    public bool UpdateExisting { get; init; }

    /// <summary>
    /// <see cref="SiteAssetsModes"/>: <c>own</c>, a <c>SysSiteAssets</c> folder below the start page as the site's assets
    /// root, as the CMS's admin mode makes one; <c>global</c>, the global assets folder. Null: <c>own</c> for a new site, and
    /// an existing site's root left as it is.
    /// </summary>
    public string? SiteAssets { get; init; }

    public bool DryRun { get; init; }

    public const string SharedRefusal =
        "opticli serve runs against a shared database here, whose site definitions the deployed development site uses too: a new site would change which site answers its host names for everyone.";

    public const string SharedHint = "Create sites of a shared database in its own admin UI (Settings > Websites).";
}

/// <param name="Name">As typed: <c>name[:port]</c>, <c>http(s)://name[:port]/</c>, or <c>*</c>.</param>
/// <param name="Type">A <see cref="HostTypes"/> name; null: primary for the first host of its language, else undefined.</param>
/// <param name="Language">The language the host is for (an enabled one); null: every language.</param>
/// <param name="Https">Overrides the scheme in <paramref name="Name"/> and <see cref="SiteAddRequest.Https"/>.</param>
public sealed record SiteAddHost(string Name, string? Type = null, string? Language = null, bool? Https = null);

/// <summary>Values of <see cref="SiteAddRequest.SiteAssets"/> and <see cref="SiteAssetsInfo.Mode"/>.</summary>
public static class SiteAssetsModes
{
    /// <summary>A folder of the site's own (<c>SysSiteAssets</c> below its start page), at <c>/siteassets/</c>.</summary>
    public const string Own = "own";

    /// <summary>The global assets folder, which every site shares (<c>/globalassets/</c>).</summary>
    public const string Global = "global";

    public const string Syntax = "own|global";

    /// <summary>The CMS's name for a site's own assets folder, and its URL segment.</summary>
    public const string FolderName = "SysSiteAssets";

    /// <returns>The mode, lower case; null when <paramref name="value"/> is neither.</returns>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Own => Own,
        Global => Global,
        _ => null,
    };
}

/// <summary>A site's assets root, as <c>sites add</c> leaves it.</summary>
/// <param name="Mode"><see cref="SiteAssetsModes"/>.</param>
/// <param name="Folder">The site's own assets folder's content id; null for <c>global</c>, and on a dry run that would create it.</param>
/// <param name="Created">The folder was created (on a dry run: would be); null when it wasn't.</param>
public sealed record SiteAssetsInfo(string Mode, int? Folder = null, bool? Created = null);

/// <summary>Response of <see cref="AgentRoutes.SiteAdd"/>.</summary>
public sealed record SiteAddResult
{
    /// <summary>The site as it is after the save (or would be, on a dry run); <see cref="SiteHostsSite.Status"/> is created, changed or unchanged.</summary>
    public required SiteHostsSite Site { get; init; }

    public bool DryRun { get; init; }

    public bool Saved { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>
/// Body of <see cref="AgentRoutes.LanguageBranches"/>: enables or disables language branches (admin mode's Manage Website
/// Languages). A code the database has no branch for gets one, enabled, with the CMS's default access rights (Everyone may
/// edit).
/// </summary>
public sealed record LanguageBranchesRequest
{
    /// <summary>Language codes, as cultures name them (<c>no</c>, <c>sv</c>, <c>en-GB</c>).</summary>
    public required IReadOnlyList<string> Codes { get; init; }

    public bool Enabled { get; init; } = true;

    public bool DryRun { get; init; }

    public const string SharedRefusal =
        "opticli serve runs against a shared database here, whose language branches the deployed development site uses too: enabling or disabling one would change it for everyone.";

    public const string SharedHint = "Change a shared database's languages in its own admin UI (Settings > Manage Website Languages).";
}

/// <summary>Response of <see cref="AgentRoutes.LanguageBranches"/>.</summary>
public sealed record LanguageBranchesResult
{
    public required IReadOnlyList<LanguageBranchChange> Languages { get; init; }

    public bool DryRun { get; init; }

    public bool Saved { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <param name="Code">The branch's code, as the CMS stores it.</param>
/// <param name="Status"><see cref="LanguageBranchStatus"/>.</param>
/// <param name="Enabled">Whether the branch is enabled after the change.</param>
public sealed record LanguageBranchChange(string Code, string Status, bool Enabled);

public static class LanguageBranchStatus
{
    /// <summary>The database had no branch for the code: one was added, enabled.</summary>
    public const string Created = "created";

    public const string Enabled = "enabled";

    public const string Disabled = "disabled";

    public const string Unchanged = "unchanged";
}
