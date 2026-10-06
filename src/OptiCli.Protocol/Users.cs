namespace OptiCli.Protocol;

/// <summary>
/// Body of <see cref="AgentRoutes.UserAdd"/>: a local login for a restored database, made through the site's ASP.NET
/// Identity (<c>UserManager</c>, <c>RoleManager</c>) and tagged as made by opticli (<see cref="LocalUsers.CreatedClaim"/>).
/// </summary>
public sealed record UserAddRequest
{
    /// <summary>The user name to sign in with.</summary>
    public required string Name { get; init; }

    /// <summary>The password; the site's password rules apply. Never logged or sent back.</summary>
    public required string Password { get; init; }

    /// <summary>Roles to put the user in; a role that doesn't exist is created. Default: <see cref="LocalUsers.DefaultRole"/>.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>Check the name, the password against the site's rules and the roles, without creating anything.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.UserAdd"/>. Never holds the password.</summary>
/// <param name="Email">The address it was given (<c>name@opticli.localhost</c>), which the site's rules may require.</param>
/// <param name="CreatedRoles">Roles that didn't exist and were (or, for a dry run, would be) created.</param>
/// <param name="UserType">The site's user class, as the CMS's UI user provider uses it.</param>
public sealed record UserAddResult(
    string Name,
    string Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> CreatedRoles,
    string UserType,
    bool Created,
    bool DryRun)
{
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.UserRemove"/>.</summary>
public sealed record UserRemoveRequest
{
    public required string Name { get; init; }

    /// <summary>Check that the user exists and opticli made it, without removing it.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.UserRemove"/>.</summary>
/// <param name="Roles">The roles it was in; they stay (other users may be in them).</param>
public sealed record UserRemoveResult(string Name, IReadOnlyList<string> Roles, bool Removed, bool DryRun);

/// <summary>
/// Response of <see cref="AgentRoutes.UserRoles"/>: the site's roles with how many users are in each, and how the CMS's
/// virtual roles map to them. No user names or addresses: those are personal data.
/// </summary>
/// <param name="UserType">The site's user class (ASP.NET Identity).</param>
public sealed record UserRolesResult(IReadOnlyList<RoleInfo> Roles, IReadOnlyList<VirtualRoleInfo> VirtualRoles, string UserType)
{
    /// <summary>How many users opticli made (<see cref="LocalUsers.CreatedClaim"/>); null when the store has no claims.</summary>
    public int? OptiCliUsers { get; init; }
}

/// <param name="Members">Users in the role.</param>
/// <param name="VirtualRoles">The virtual roles it gives its members (<c>CmsAdmins</c>, <c>CmsEditors</c>, ...).</param>
public sealed record RoleInfo(string Name, int Members, IReadOnlyList<string> VirtualRoles);

/// <summary>A virtual role: one that maps to other roles (<c>EPiServer:Cms:MappedRoles</c>), or a built-in one decided in code.</summary>
/// <param name="Kind"><c>mapped</c>, or the provider's class name (<c>EveryoneRole</c>, <c>AuthenticatedRole</c>, ...).</param>
/// <param name="Roles">For a mapped role, the roles that give it.</param>
/// <param name="MatchAll">For a mapped role: a user needs every one of <see cref="Roles"/>, not one.</param>
public sealed record VirtualRoleInfo(string Name, string Kind, IReadOnlyList<string>? Roles = null, bool? MatchAll = null);

/// <summary>Rules the CLI and the site agent share for <c>opticli users</c>.</summary>
public static class LocalUsers
{
    /// <summary>The role <c>users add</c> puts a user in by default: what the CMS's own first-admin registration uses.</summary>
    public const string DefaultRole = "WebAdmins";

    /// <summary>The claim that marks a user opticli made; <c>users remove</c> only removes users that have it.</summary>
    public const string CreatedClaim = "opticli:created";

    /// <summary>
    /// The domain of the address a user opticli makes gets: <c>.localhost</c> never reaches anyone, and it marks such users
    /// on a site whose user store keeps no claims.
    /// </summary>
    public const string EmailDomain = "opticli.localhost";

    public const string SharedRefusal =
        "Users aren't added or removed against a shared database: its users are real, and the deployed site signs them in.";

    public const string SharedHint = "Add a user in that environment's own admin UI, or restore a copy of the database locally.";

    /// <summary>The address a new user gets: its name with anything but letters, digits, '.', '_' and '-' as '-'.</summary>
    public static string Email(string name)
    {
        var local = new string(name.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray()).Trim('.');
        return $"{(local.Length == 0 ? "user" : local)}@{EmailDomain}";
    }
}
