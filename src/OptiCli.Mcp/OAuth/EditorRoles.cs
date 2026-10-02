using System.Security.Claims;
using EPiServer.Security;
using EPiServer.Shell.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Where an editor's roles and account come from. A login cookie keeps the roles it was issued with, and keeps working
/// for a while after the account is disabled, so the module asks this instead, at consent and on every token refresh:
/// an editor who loses their role, or whose account is disabled or deleted, stops getting tokens.
/// </summary>
public interface IEditorRoles
{
    /// <returns>The roles the user has now, as the site's role store has them; null when the store can't say.</returns>
    Task<IReadOnlyList<string>?> GetRolesAsync(string userName, CancellationToken cancellationToken);

    /// <returns>
    /// Whether the user's account is active now: true when it is, false when it is disabled (not approved, locked out)
    /// or doesn't exist any more, null when the user store can't say.
    /// </returns>
    Task<bool?> IsActiveAsync(string userName, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="user"/> is in <paramref name="role"/>, counting the CMS's virtual roles (CmsEditors,
    /// CmsAdmins and the site's own mapped roles), which aren't stored with the user.
    /// </summary>
    bool IsInRole(ClaimsPrincipal user, string role);
}

/// <summary>
/// The CMS's own answer: <see cref="UIRoleProvider"/> and <see cref="UIUserProvider"/>, the stores the CMS UI's user
/// admin uses, which answer for ASP.NET Identity users and for users synchronized from an external login (Entra ID,
/// Opti ID) alike.
/// </summary>
internal sealed class CmsEditorRoles(IServiceProvider services, ILogger<CmsEditorRoles> logger) : IEditorRoles
{
    private static int _warned;

    private static int _warnedUsers;

    public async Task<bool?> IsActiveAsync(string userName, CancellationToken cancellationToken)
    {
        if (services.GetService<UIUserProvider>() is not { } provider)
        {
            WarnOnce(ref _warnedUsers, "No UIUserProvider is registered: the MCP module can't tell whether an editor's account is still active, and goes by their roles alone.");
            return null;
        }
        IUIUser? user;
        try
        {
            user = await provider.GetUserAsync(userName);
        }
        catch (NotSupportedException)
        {
            // The base class's answer for a provider that doesn't look users up.
            WarnOnce(ref _warnedUsers, $"{provider.GetType().FullName} doesn't look users up: the MCP module can't tell whether an editor's account is still active, and goes by their roles alone.");
            return null;
        }
        return user is { IsApproved: true, IsLockedOut: false };
    }

    private void WarnOnce(ref int warned, string message)
    {
        if (Interlocked.Exchange(ref warned, 1) == 0)
        {
            logger.LogWarning("{Problem}", message);
        }
    }

    public async Task<IReadOnlyList<string>?> GetRolesAsync(string userName, CancellationToken cancellationToken)
    {
        if (services.GetService<UIRoleProvider>() is not { } provider)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                logger.LogWarning("No UIRoleProvider is registered: the MCP module falls back to the roles in the editor's login.");
            }
            return null;
        }
        var roles = new List<string>();
        await foreach (var role in provider.GetRolesForUserAsync(userName).WithCancellation(cancellationToken))
        {
            roles.Add(role);
        }
        return roles;
    }

    public bool IsInRole(ClaimsPrincipal user, string role) => user.IsInRole(role) || VirtualRolePrincipal.CreateWrapper(user).IsInRole(role);
}

/// <summary>Why <see cref="EditorGate.RefusalAsync"/> turned a user away.</summary>
internal enum GateRefusal
{
    /// <summary>None of <see cref="OptiCliMcpOptions.AllowedRoles"/>.</summary>
    NotAnEditor,

    /// <summary>The account is disabled (not approved, locked out) or gone.</summary>
    AccountDisabled,
}

/// <summary>The role gate: who may connect an assistant, and who administers connections.</summary>
internal sealed class EditorGate(IEditorRoles roles)
{
    /// <summary>The roles that see and revoke every editor's connections, as in the CMS's own admin views.</summary>
    public static readonly string[] AdminRoles = ["WebAdmins", "CmsAdmins"];

    /// <summary>
    /// The signed-in user with their roles as they are now: their name, and the roles the role store has for them (or,
    /// when it can't say, the roles in their login). Virtual roles the login added as claims are left out: the CMS works
    /// them out again on every check, so they aren't frozen into a grant.
    /// </summary>
    public async Task<(ClaimsPrincipal Principal, IReadOnlyList<string> Roles)> CurrentAsync(ClaimsPrincipal signedIn, CancellationToken cancellationToken)
    {
        var name = signedIn.Identity?.Name ?? "";
        var current = await roles.GetRolesAsync(name, cancellationToken)
            ?? signedIn.Identities.SelectMany(i => i.FindAll(i.RoleClaimType)).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (Principal(name, current, signedIn.Identity?.AuthenticationType), current);
    }

    /// <summary>The roles a user has now, for a refresh, where nobody is signed in; <paramref name="fallback"/> when the role store can't say.</summary>
    public async Task<IReadOnlyList<string>> CurrentRolesAsync(string userName, IReadOnlyList<string> fallback, CancellationToken cancellationToken) =>
        await roles.GetRolesAsync(userName, cancellationToken) ?? fallback;

    /// <summary>Whether a user with these roles may connect: one of <see cref="OptiCliMcpOptions.AllowedRoles"/>.</summary>
    public bool MayConnect(ClaimsPrincipal user, OptiCliMcpOptions options) =>
        user.Identity?.IsAuthenticated == true && options.AllowedRoles.Any(role => roles.IsInRole(user, role));

    /// <summary>
    /// The whole gate, at consent and at a refresh: the roles (<see cref="MayConnect"/>), then the account, which must
    /// still be active (a login cookie outlives disabling it). An account the user store can't say anything about goes by
    /// its roles alone.
    /// </summary>
    /// <param name="user">The user with their current roles (<see cref="CurrentAsync"/>, <see cref="Principal"/>).</param>
    /// <returns>Null when the user may connect; otherwise why not, for the audit log.</returns>
    public async Task<GateRefusal?> RefusalAsync(ClaimsPrincipal user, OptiCliMcpOptions options, CancellationToken cancellationToken)
    {
        if (!MayConnect(user, options))
        {
            return GateRefusal.NotAnEditor;
        }
        return await roles.IsActiveAsync(user.Identity?.Name ?? "", cancellationToken) == false ? GateRefusal.AccountDisabled : null;
    }

    public bool IsAdmin(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && AdminRoles.Any(role => roles.IsInRole(user, role));

    /// <summary>A principal with just a name and roles: what the gate and the CMS judge an editor by.</summary>
    public static ClaimsPrincipal Principal(string name, IEnumerable<string> roleNames, string? authenticationType, IEnumerable<Claim>? extra = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roleNames.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(extra ?? []);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType ?? "OptiCliMcp", ClaimTypes.Name, ClaimTypes.Role));
    }
}
