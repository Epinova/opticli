using System.Reflection;
using System.Security.Claims;
using EPiServer.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Users;

/// <summary>
/// <c>POST /v1/users/add|remove</c> and <c>GET /v1/users/roles</c>: a local login for a restored database, made through
/// the site's own ASP.NET Identity (<see cref="UserManager{TUser}"/>, <see cref="RoleManager{TRole}"/>), as the CMS's
/// first-admin registration does.
/// </summary>
/// <remarks>
/// <para>Here and not in <c>OptiCli.Cms</c>, which the MCP module compiles in: users are the developer's only, and a
/// production site must never be able to get one through opticli.</para>
/// <para>The agent doesn't reference the CMS's ASP.NET Identity package, and sites subclass its <c>ApplicationUser</c>,
/// so the user class is found at runtime (<see cref="UserType"/>) and the work done by a generic method made for it.</para>
/// <para>opticli never changes a user it didn't make: it only adds users, tagged with <see cref="LocalUsers.CreatedClaim"/>
/// (and an address at <see cref="LocalUsers.EmailDomain"/>), and only removes users with that tag.</para>
/// </remarks>
internal static class UsersOperation
{
    /// <summary>The CMS UI's user provider (EPiServer.Shell): <c>ApplicationUserProvider&lt;TUser&gt;</c> for ASP.NET Identity.</summary>
    private const string UiUserProvider = "EPiServer.Shell.Security.UIUserProvider";

    public static Task<UserAddResult> AddAsync(AgentRequest request, UserAddRequest body) =>
        Run<UserAddResult>(request, nameof(AddUserAsync), body);

    public static Task<UserRemoveResult> RemoveAsync(AgentRequest request, UserRemoveRequest body) =>
        Run<UserRemoveResult>(request, nameof(RemoveUserAsync), body);

    public static Task<UserRolesResult> RolesAsync(AgentRequest request) =>
        Run<UserRolesResult>(request, nameof(ListRolesAsync), null);

    private static async Task<T> Run<T>(AgentRequest request, string method, object? body)
    {
        if (request.Service<AgentSettings>().SharedDatabase)
        {
            throw AgentException.Refused(LocalUsers.SharedRefusal, LocalUsers.SharedHint);
        }
        var services = request.Context.RequestServices;
        var type = RequireUserType(services, ProviderBase());
        var generic = typeof(UsersOperation).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
        try
        {
            return await (Task<T>)generic.Invoke(null, [services, body])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    /// <summary>The CMS UI's user provider type, from whichever loaded assembly has it; null on a site without the CMS UI.</summary>
    private static Type? ProviderBase() =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(UiUserProvider, throwOnError: false)).FirstOrDefault(t => t is not null);

    /// <exception cref="AgentException"><c>refused</c> on a site whose CMS users don't come from ASP.NET Identity.</exception>
    internal static Type RequireUserType(IServiceProvider services, Type? providerBase)
    {
        var provider = providerBase is null ? null : services.GetService(providerBase);
        return UserType(services, provider) ?? throw AgentException.Refused(
            $"This site's CMS users don't come from ASP.NET Identity{(provider is null ? "" : $" (its user provider is {Describe(provider.GetType())})")}, so opticli can't add a local login.",
            "Sign in through the site's identity provider (OpenID Connect, Opti ID, ...) with an account that has access there; opticli only adds users to ASP.NET Identity (services.AddCmsAspNetIdentity).");
    }

    /// <summary>
    /// The user class the CMS UI's user provider is generic over (<c>ApplicationUserProvider&lt;ApplicationUser&gt;</c>,
    /// or a site's subclass), when the site also has a <see cref="UserManager{TUser}"/> for it; null otherwise.
    /// </summary>
    internal static Type? UserType(IServiceProvider services, object? provider)
    {
        for (var type = provider?.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericArguments().FirstOrDefault(typeof(IdentityUser).IsAssignableFrom) is { } user
                && services.GetService(typeof(UserManager<>).MakeGenericType(user)) is not null)
            {
                return user;
            }
        }
        return null;
    }

    internal static async Task<UserAddResult> AddUserAsync<TUser>(IServiceProvider services, UserAddRequest body) where TUser : IdentityUser, new()
    {
        var users = services.GetRequiredService<UserManager<TUser>>();
        var roleManager = RoleManager(services);
        var name = RequireName(body.Name);
        var roles = (body.Roles is { Count: > 0 } given ? given : [LocalUsers.DefaultRole]).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roles.Any(r => r.Length == 0))
        {
            throw AgentException.Usage("A role name is empty.");
        }
        if (await users.FindByNameAsync(name) is not null)
        {
            throw AgentException.Conflict($"A user named '{name}' exists already.", "Choose another name; opticli never changes a user it didn't make (`opticli users remove` removes one it made).");
        }

        var user = new TUser { UserName = name, Email = LocalUsers.Email(name), EmailConfirmed = true };
        // The CMS's ApplicationUser has these; the UI's user provider sets IsApproved when it creates a user.
        Set(user, "IsApproved", true);
        Set(user, "Comment", "Made by opticli for local development (opticli users add).");

        var problems = new List<ValidationIssue>();
        foreach (var validator in users.UserValidators)
        {
            problems.AddRange(Issues("name", await validator.ValidateAsync(users, user)));
        }
        foreach (var validator in users.PasswordValidators)
        {
            problems.AddRange(Issues("password", await validator.ValidateAsync(users, user, body.Password)));
        }
        if (problems.Count > 0)
        {
            throw AgentException.Invalid(problems, "The site's ASP.NET Identity rules (IdentityOptions) decide these; give a password that meets them, or another name. Nothing was created.", AgentErrorReasons.Users);
        }

        var missing = new List<string>();
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                missing.Add(role);
            }
        }
        var warnings = Warnings(services, roles);
        if (body.DryRun)
        {
            return new UserAddResult(name, user.Email!, roles, missing, Describe(typeof(TUser)), Created: false, DryRun: true) { Warnings = warnings };
        }

        Check(await users.CreateAsync(user, body.Password), "password");
        try
        {
            if (users.SupportsUserClaim)
            {
                Check(await users.AddClaimAsync(user, new Claim(LocalUsers.CreatedClaim, LocalUsers.CreatedClaimValue)), null);
            }
            foreach (var role in missing)
            {
                Check(await roleManager.CreateAsync(new IdentityRole(role)), "role");
            }
            Check(await users.AddToRolesAsync(user, roles), "role");
        }
        catch
        {
            // Not half a user: the one made just now goes again (roles made for it stay, empty).
            await users.DeleteAsync(user);
            throw;
        }
        return new UserAddResult(name, user.Email!, roles, missing, Describe(typeof(TUser)), Created: true, DryRun: false) { Warnings = warnings };
    }

    internal static async Task<UserRemoveResult> RemoveUserAsync<TUser>(IServiceProvider services, UserRemoveRequest body) where TUser : IdentityUser, new()
    {
        var users = services.GetRequiredService<UserManager<TUser>>();
        var name = RequireName(body.Name);
        var user = await users.FindByNameAsync(name) ?? throw AgentException.NotFound($"No user is named '{name}'.");
        if (!await MadeByOptiCliAsync(users, user))
        {
            throw AgentException.Refused(
                $"'{name}' wasn't made by opticli, so opticli won't remove it.",
                "Remove it in the CMS admin UI (Users) if it should go; opticli only removes users `opticli users add` made.");
        }
        var roles = (await users.GetRolesAsync(user)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!body.DryRun)
        {
            Check(await users.DeleteAsync(user), null);
        }
        return new UserRemoveResult(name, roles, Removed: !body.DryRun, DryRun: body.DryRun);
    }

    internal static async Task<UserRolesResult> ListRolesAsync<TUser>(IServiceProvider services, object? _) where TUser : IdentityUser, new()
    {
        var users = services.GetRequiredService<UserManager<TUser>>();
        var roleManager = RoleManager(services);
        var virtualRoles = VirtualRoles(services);
        var roles = new List<RoleInfo>();
        if (roleManager.SupportsQueryableRoles)
        {
            foreach (var role in roleManager.Roles.Select(r => r.Name).ToList().OfType<string>().Order(StringComparer.OrdinalIgnoreCase))
            {
                // Only how many: who they are is personal data.
                var members = (await users.GetUsersInRoleAsync(role)).Count;
                roles.Add(new RoleInfo(role, members, virtualRoles.Where(v => v.Roles?.Contains(role, StringComparer.OrdinalIgnoreCase) == true).Select(v => v.Name).ToList()));
            }
        }
        int? created = users.SupportsUserClaim ? (await users.GetUsersForClaimAsync(new Claim(LocalUsers.CreatedClaim, LocalUsers.CreatedClaimValue))).Count : null;
        return new UserRolesResult(roles, virtualRoles, Describe(typeof(TUser))) { OptiCliUsers = created };
    }

    /// <summary>
    /// Tagged with <see cref="LocalUsers.CreatedClaim"/> (value <c>true</c>) and an address at
    /// <see cref="LocalUsers.EmailDomain"/>, as <c>users add</c> makes them; in a store without claims, by the address alone.
    /// </summary>
    internal static async Task<bool> MadeByOptiCliAsync<TUser>(UserManager<TUser> users, TUser user) where TUser : IdentityUser
    {
        var address = user.Email?.EndsWith("@" + LocalUsers.EmailDomain, StringComparison.OrdinalIgnoreCase) == true;
        if (!users.SupportsUserClaim)
        {
            return address;
        }
        return address && (await users.GetClaimsAsync(user)).Any(c => c.Type == LocalUsers.CreatedClaim && c.Value == LocalUsers.CreatedClaimValue);
    }

    /// <summary>The CMS registers <c>RoleManager&lt;IdentityRole&gt;</c> with ASP.NET Identity (<c>AddCmsAspNetIdentity</c>).</summary>
    private static RoleManager<IdentityRole> RoleManager(IServiceProvider services) =>
        services.GetService<RoleManager<IdentityRole>>() ?? throw AgentException.Refused(
            "This site's ASP.NET Identity has no RoleManager<IdentityRole>, so opticli can't check or create roles.",
            "Add the user in the CMS admin UI (Users) instead.");

    /// <summary>
    /// When <c>CmsAdmins</c> is mapped to roles none of which the user gets, it gets no admin mode; say which roles would.
    /// </summary>
    private static IReadOnlyList<string>? Warnings(IServiceProvider services, IReadOnlyList<string> roles)
    {
        var admins = VirtualRoles(services).FirstOrDefault(v => v.Name == "CmsAdmins" && v.Roles is not null);
        return admins is { Roles: { } mapped } && !roles.Any(r => mapped.Contains(r, StringComparer.OrdinalIgnoreCase))
            ? [$"On this site CmsAdmins (admin mode) maps to {string.Join(", ", mapped)}, none of which the user gets; add --role {mapped.First()} for admin mode."]
            : null;
    }

    /// <summary>The CMS's virtual roles; for a mapped one (<see cref="MappedRole"/>) the roles that give it.</summary>
    internal static IReadOnlyList<VirtualRoleInfo> VirtualRoles(IServiceProvider services)
    {
        if (services.GetService<IVirtualRoleRepository>() is not { } repository)
        {
            return [];
        }
        var result = new List<VirtualRoleInfo>();
        foreach (var name in repository.GetAllRoles().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!repository.TryGetRole(name, out var role))
            {
                continue;
            }
            result.Add(role is MappedRole mapped
                ? new VirtualRoleInfo(name, "mapped", (mapped.Roles ?? []).ToList(), mapped.ShouldMatchAll)
                : new VirtualRoleInfo(name, role.GetType().Name));
        }
        return result;
    }

    private static string RequireName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? throw AgentException.Usage("The user name is empty.") : name.Trim();

    private static IEnumerable<ValidationIssue> Issues(string property, IdentityResult result) =>
        result.Succeeded ? [] : result.Errors.Select(e => new ValidationIssue(property, e.Description));

    /// <exception cref="AgentException"><c>validation</c> with ASP.NET Identity's reasons.</exception>
    private static void Check(IdentityResult result, string? property)
    {
        if (!result.Succeeded)
        {
            throw AgentException.Invalid(Issues(property ?? "user", result).ToList(), "ASP.NET Identity refused it; its reasons are listed. A user it refused to make isn't left half made.", AgentErrorReasons.Users);
        }
    }

    private static void Set(object target, string property, object value)
    {
        if (target.GetType().GetProperty(property) is { CanWrite: true } info && info.PropertyType.IsInstanceOfType(value))
        {
            info.SetValue(target, value);
        }
    }

    private static string Describe(Type type) =>
        type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>" : type.FullName ?? type.Name;
}
