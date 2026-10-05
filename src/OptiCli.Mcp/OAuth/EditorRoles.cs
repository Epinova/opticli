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
    /// Whether the user's account is active now: true when it is, false when the site's own user store has it disabled
    /// (not approved, locked out) or doesn't have it any more, null when the site can't say, as for a user who signs in
    /// with an external login, whose account the identity provider manages. Only false turns the user away: null leaves
    /// it to their roles.
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
/// <remarks>
/// <para>
/// Whether an account is active (<see cref="IsActiveAsync"/>) is only the site's to say for accounts the site itself
/// manages: ASP.NET Identity's (the CMS's <c>ApplicationUserProvider&lt;TUser&gt;</c>), where an administrator disables,
/// locks out or deletes a user in the CMS. A user synchronized from an external login (Entra ID, Opti ID, any OpenID
/// Connect provider) is disabled in the identity provider instead, which the CMS never hears about; a user provider for
/// such users, as some sites register, typically knows no more than the name, and reports everyone as not approved.
/// The CMS itself treats a site without a user provider as one whose users aren't managed in the CMS at all.
/// </para>
/// <para>The rule, in order:</para>
/// <list type="number">
/// <item>No user provider, or one that isn't ASP.NET Identity's: unknown (null); the roles decide.</item>
/// <item>The user is in ASP.NET Identity's store, approved and not locked out: active.</item>
/// <item>
/// The user is synchronized from an external login (the CMS's <see cref="SynchronizingRolesSecurityEntityProvider"/>
/// has them): unknown, as on a site with both kinds of login, whatever ASP.NET Identity's store has under that name.
/// </item>
/// <item>Otherwise the account is disabled, locked out, or deleted from ASP.NET Identity's store: not active.</item>
/// </list>
/// <para>
/// Unknown isn't a free pass: the roles still decide at every refresh, and <see cref="OptiCliMcpOptions.ConnectionLifetime"/>
/// bounds how long a connection lasts without the editor signing in again through the site's login, where the identity
/// provider has its say.
/// </para>
/// </remarks>
internal sealed class CmsEditorRoles(IServiceProvider services, ILogger<CmsEditorRoles> logger) : IEditorRoles
{
    /// <summary>The <see cref="UIUserProvider.Name"/> of ASP.NET Identity's user provider, <c>ApplicationUserProvider&lt;TUser&gt;</c>.</summary>
    internal const string AspNetIdentityProviderName = "EPi_AspNetIdentityUserProvider";

    /// <summary>
    /// ASP.NET Identity's user provider, by name: it is in <c>EPiServer.CMS.UI.AspNetIdentity</c>, which a site with only
    /// an external login doesn't have, so the module doesn't reference it.
    /// </summary>
    private const string AspNetIdentityProviderType = "EPiServer.Cms.UI.AspNetIdentity.ApplicationUserProvider`1";

    private static int _warned;

    private static int _warnedUsers;

    private static int _warnedExternal;

    private static int _warnedSynchronized;

    public async Task<bool?> IsActiveAsync(string userName, CancellationToken cancellationToken)
    {
        if (services.GetService<UIUserProvider>() is not { } provider)
        {
            WarnOnce(ref _warnedUsers, LogLevel.Information, "No UIUserProvider is registered: the MCP module can't tell whether an editor's account is still active, and goes by their roles alone.");
            return null;
        }
        if (!ManagesAccounts(provider))
        {
            WarnOnce(ref _warnedExternal, LogLevel.Information,
                $"{provider.GetType().FullName} isn't ASP.NET Identity's user store: the MCP module leaves whether an editor's account is active to the identity provider, and goes by their roles and OptiCli:Mcp:ConnectionLifetime.");
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
            WarnOnce(ref _warnedUsers, LogLevel.Warning, $"{provider.GetType().FullName} doesn't look users up: the MCP module can't tell whether an editor's account is still active, and goes by their roles alone.");
            return null;
        }
        if (user is { IsApproved: true, IsLockedOut: false })
        {
            return true;
        }
        // Disabled or missing in the site's own store: refused, unless it is someone who signs in with an external login.
        return await IsSynchronizedAsync(userName) == false ? false : null;
    }

    /// <summary>Whether the provider manages accounts itself, so that its <see cref="IUIUser.IsApproved"/> and <see cref="IUIUser.IsLockedOut"/> mean something.</summary>
    internal static bool ManagesAccounts(UIUserProvider provider)
    {
        if (string.Equals(provider.Name, AspNetIdentityProviderName, StringComparison.Ordinal))
        {
            return true;
        }
        for (var type = provider.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == AspNetIdentityProviderType)
            {
                return true;
            }
        }
        return false;
    }

    /// <returns>
    /// Whether the CMS synchronized <paramref name="userName"/> from an external login (they signed in through one at
    /// least once); null when it can't say.
    /// </returns>
    /// <remarks>
    /// <see cref="SynchronizingRolesSecurityEntityProvider"/> is the CMS's store of synchronized users, searched the way
    /// the CMS's access rights dialog does: by part of the name or e-mail address, so only an exact match counts.
    /// </remarks>
    internal async Task<bool?> IsSynchronizedAsync(string userName)
    {
        if (services.GetService<SynchronizingRolesSecurityEntityProvider>() is not { } synchronized)
        {
            WarnOnce(ref _warnedSynchronized, LogLevel.Warning, "SynchronizingRolesSecurityEntityProvider isn't registered: the MCP module can't tell synchronized users from deleted ones, and goes by their roles alone.");
            return null;
        }
        var found = await synchronized.SearchAsync(userName, ClaimTypes.Name);
        return found.Any(e => e.EntityType == SecurityEntityType.User && string.Equals(e.Name, userName, StringComparison.OrdinalIgnoreCase));
    }

    private void WarnOnce(ref int warned, LogLevel level, string message)
    {
        if (Interlocked.Exchange(ref warned, 1) == 0)
        {
            logger.Log(level, "{Problem}", message);
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
