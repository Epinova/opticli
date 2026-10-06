using System.Security.Claims;
using EPiServer.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Agent.Users;
using OptiCli.Cms;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Users;

/// <summary>
/// <c>users add|remove|roles</c> against ASP.NET Identity's own <see cref="UserManager{TUser}"/> and
/// <see cref="RoleManager{TRole}"/> over in-memory stores: what is made, what is refused, and what is never shown.
/// </summary>
public class UsersOperationTests
{
    /// <summary>A site's user class, as sites subclass the CMS's <c>ApplicationUser</c>.</summary>
    public sealed class SiteUser : IdentityUser
    {
        public bool IsApproved { get; set; }

        public string? Comment { get; set; }
    }

    /// <summary>Stands in for the CMS UI's user provider (<c>UIUserProvider</c>), which the agent doesn't reference.</summary>
    public abstract class UiUserProvider;

    /// <summary>Stands in for <c>ApplicationUserProvider&lt;TUser&gt;</c>.</summary>
    public sealed class IdentityProvider<TUser> : UiUserProvider;

    /// <summary>An OpenID Connect site's provider: not generic over a user class.</summary>
    public sealed class SynchronizingProvider : UiUserProvider;

    private readonly UserStore _users = new();

    private readonly RoleStore _roles = new();

    private readonly ServiceProvider _services;

    public UsersOperationTests()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIdentityCore<SiteUser>(o => o.User.RequireUniqueEmail = true).AddRoles<IdentityRole>();
        services.AddSingleton<IUserStore<SiteUser>>(_users);
        services.AddSingleton<IRoleStore<IdentityRole>>(_roles);
        services.AddSingleton<UiUserProvider>(new IdentityProvider<SiteUser>());
        services.AddSingleton<IVirtualRoleRepository>(new VirtualRoles(new Dictionary<string, string[]>
        {
            ["CmsAdmins"] = ["WebAdmins", "Administrators"],
            ["CmsEditors"] = ["WebEditors"],
        }));
        _services = services.BuildServiceProvider();
    }

    private Task<UserAddResult> Add(string name, string password = "Secret-123", bool dryRun = false, params string[] roles) =>
        UsersOperation.AddUserAsync<SiteUser>(_services, new UserAddRequest { Name = name, Password = password, Roles = roles.Length > 0 ? roles : null, DryRun = dryRun });

    private Task<UserRemoveResult> Remove(string name, bool dryRun = false) =>
        UsersOperation.RemoveUserAsync<SiteUser>(_services, new UserRemoveRequest { Name = name, DryRun = dryRun });

    [Fact]
    public void The_user_class_is_the_one_the_CMS_UIs_user_provider_is_generic_over()
    {
        Assert.Equal(typeof(SiteUser), UsersOperation.RequireUserType(_services, typeof(UiUserProvider)));
    }

    [Fact]
    public void A_site_whose_users_come_from_elsewhere_is_refused_naming_its_provider()
    {
        var services = new ServiceCollection().AddSingleton<UiUserProvider>(new SynchronizingProvider()).BuildServiceProvider();

        var ex = Assert.Throws<AgentException>(() => UsersOperation.RequireUserType(services, typeof(UiUserProvider)));

        Assert.Equal(AgentErrorCodes.Refused, ex.Code);
        Assert.Contains(nameof(SynchronizingProvider), ex.Message);
        Assert.Contains("identity provider", ex.Hint);
        Assert.Equal(AgentErrorCodes.Refused, Assert.Throws<AgentException>(() => UsersOperation.RequireUserType(services, providerBase: null)).Code);
    }

    [Fact]
    public async Task A_user_is_made_approved_tagged_and_in_WebAdmins_which_is_created_when_missing()
    {
        var result = await Add("dev");

        Assert.Equal(("dev", "dev@opticli.localhost", true, false), (result.Name, result.Email, result.Created, result.DryRun));
        Assert.Equal([LocalUsers.DefaultRole], result.Roles);
        Assert.Equal([LocalUsers.DefaultRole], result.CreatedRoles);
        var user = _users.Users.Single();
        Assert.True(user.IsApproved);
        Assert.True(user.EmailConfirmed);
        Assert.Contains("opticli", user.Comment);
        Assert.NotEqual("Secret-123", user.PasswordHash);
        Assert.Contains(_users.Claims[user.Id], c => c.Type == LocalUsers.CreatedClaim);
        Assert.Equal(["WEBADMINS"], _users.Roles[user.Id]);
        Assert.Null(result.Warnings);
    }

    [Fact]
    public async Task A_dry_run_checks_the_password_rules_and_makes_nothing()
    {
        var dry = await Add("dev", dryRun: true, roles: ["WebEditors"]);
        Assert.Equal((false, true), (dry.Created, dry.DryRun));
        Assert.Equal(["WebEditors"], dry.CreatedRoles);
        Assert.Empty(_users.Users);
        Assert.Empty(_roles.Roles);

        var weak = await Assert.ThrowsAsync<AgentException>(() => Add("dev", password: "short", dryRun: true));
        Assert.Equal((AgentErrorCodes.Validation, AgentErrorReasons.Users), (weak.Code, weak.Reason));
        Assert.All(weak.Validation!, v => Assert.Equal("password", v.Property));
        Assert.DoesNotContain(weak.Validation!, v => v.Message.Contains("short", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Roles_that_dont_give_admin_mode_are_flagged()
    {
        var result = await Add("editor", roles: ["WebEditors"]);

        Assert.Contains("CmsAdmins (admin mode) maps to WebAdmins, Administrators", Assert.Single(result.Warnings!));
    }

    [Fact]
    public async Task An_existing_name_is_a_conflict()
    {
        await Add("dev");

        var ex = await Assert.ThrowsAsync<AgentException>(() => Add("DEV"));

        Assert.Equal(AgentErrorCodes.Conflict, ex.Code);
        Assert.Single(_users.Users);
    }

    [Fact]
    public async Task Only_users_opticli_made_are_removed_and_their_roles_stay()
    {
        await Add("dev", roles: ["WebAdmins", "WebEditors"]);
        var other = new SiteUser { UserName = "someone", Email = "someone@example.com" };
        await _services.GetRequiredService<UserManager<SiteUser>>().CreateAsync(other);

        var refused = await Assert.ThrowsAsync<AgentException>(() => Remove("someone"));
        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Equal(AgentErrorCodes.NotFound, (await Assert.ThrowsAsync<AgentException>(() => Remove("nobody"))).Code);

        var dry = await Remove("dev", dryRun: true);
        Assert.Equal((false, 2), (dry.Removed, _users.Users.Count));

        var removed = await Remove("dev");
        Assert.True(removed.Removed);
        Assert.Equal(["WebAdmins", "WebEditors"], removed.Roles);
        Assert.Equal("someone", Assert.Single(_users.Users).UserName);
        Assert.Equal(2, _roles.Roles.Count);
    }

    [Fact]
    public async Task Roles_are_counted_never_named_by_member()
    {
        await Add("dev");
        await Add("dev2", roles: ["WebEditors"]);

        var result = await UsersOperation.ListRolesAsync<SiteUser>(_services, null);

        Assert.Equal(
            [("WebAdmins", 1, "CmsAdmins"), ("WebEditors", 1, "CmsEditors")],
            result.Roles.Select(r => (r.Name, r.Members, string.Join(",", r.VirtualRoles))));
        Assert.Equal(2, result.OptiCliUsers);
        Assert.Contains(result.VirtualRoles, v => v is { Name: "CmsAdmins", Kind: "mapped" } && v.Roles!.SequenceEqual(["WebAdmins", "Administrators"]));
        var json = System.Text.Json.JsonSerializer.Serialize(result, AgentJson.Options);
        Assert.DoesNotContain("dev", json);
        Assert.DoesNotContain("opticli.localhost", json);
    }

    [Fact]
    public async Task Nothing_is_done_against_a_shared_database()
    {
        var services = new ServiceCollection().AddSingleton(Settings(pinned: Remote, approvedRemote: RemoteApproval)).BuildServiceProvider();
        var request = new AgentRequest(new DefaultHttpContext { RequestServices = services }, null);

        var ex = await Assert.ThrowsAsync<AgentException>(() => UsersOperation.AddAsync(request, new UserAddRequest { Name = "dev", Password = "Secret-123" }));

        Assert.Equal((AgentErrorCodes.Refused, LocalUsers.SharedRefusal), (ex.Code, ex.Message));
    }

    [Theory]
    [InlineData("dev", "dev@opticli.localhost")]
    [InlineData("dev@example.com", "dev-example.com@opticli.localhost")]
    [InlineData("Kari Nordmann", "Kari-Nordmann@opticli.localhost")]
    [InlineData("...", "user@opticli.localhost")]
    public void A_new_user_gets_an_address_that_reaches_nobody(string name, string email)
    {
        Assert.Equal(email, LocalUsers.Email(name));
    }

    /// <summary>The CMS's virtual roles, mapped ones only.</summary>
    private sealed class VirtualRoles(Dictionary<string, string[]> mapped) : IVirtualRoleRepository
    {
        public IEnumerable<string> GetAllRoles() => mapped.Keys;

        public IEnumerable<string> GetRoleNamesByType(Type type) => [];

        public bool TryGetRole(string role, out VirtualRoleProviderBase virtualRole)
        {
            var found = new MappedRole(this) { Name = role, Roles = mapped.GetValueOrDefault(role) ?? [] };
            virtualRole = found;
            return mapped.ContainsKey(role);
        }

        public IEnumerable<string> SearchRoles(string query) => [];

        public void Register(string name, Type virtualRoleProviderType, bool replicateChanges) => throw new NotSupportedException();

        public void Register(string name, VirtualRoleProviderBase provider) => throw new NotSupportedException();

        public void Unregister(string name, bool replicateChanges) => throw new NotSupportedException();
    }

    /// <summary>Users, their password hashes, claims and roles (by normalized name), in memory.</summary>
    private sealed class UserStore : IUserPasswordStore<SiteUser>, IUserClaimStore<SiteUser>, IUserRoleStore<SiteUser>, IUserEmailStore<SiteUser>
    {
        public List<SiteUser> Users { get; } = [];

        public Dictionary<string, List<Claim>> Claims { get; } = [];

        public Dictionary<string, List<string>> Roles { get; } = [];

        public void Dispose()
        {
        }

        public Task<IdentityResult> CreateAsync(SiteUser user, CancellationToken cancellationToken)
        {
            Users.Add(user);
            Claims[user.Id] = [];
            Roles[user.Id] = [];
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<IdentityResult> DeleteAsync(SiteUser user, CancellationToken cancellationToken)
        {
            Users.Remove(user);
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<IdentityResult> UpdateAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);

        public Task<SiteUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));

        public Task<SiteUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
            Task.FromResult(Users.FirstOrDefault(u => u.NormalizedUserName == normalizedUserName));

        public Task<string?> GetNormalizedUserNameAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);

        public Task<string> GetUserIdAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

        public Task<string?> GetUserNameAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);

        public Task SetNormalizedUserNameAsync(SiteUser user, string? normalizedName, CancellationToken cancellationToken)
        {
            user.NormalizedUserName = normalizedName;
            return Task.CompletedTask;
        }

        public Task SetUserNameAsync(SiteUser user, string? userName, CancellationToken cancellationToken)
        {
            user.UserName = userName;
            return Task.CompletedTask;
        }

        public Task<string?> GetPasswordHashAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

        public Task<bool> HasPasswordAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash is not null);

        public Task SetPasswordHashAsync(SiteUser user, string? passwordHash, CancellationToken cancellationToken)
        {
            user.PasswordHash = passwordHash;
            return Task.CompletedTask;
        }

        public Task AddClaimsAsync(SiteUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
        {
            Claims[user.Id].AddRange(claims);
            return Task.CompletedTask;
        }

        public Task<IList<Claim>> GetClaimsAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult<IList<Claim>>(Claims.GetValueOrDefault(user.Id) ?? []);

        public Task<IList<SiteUser>> GetUsersForClaimAsync(Claim claim, CancellationToken cancellationToken) =>
            Task.FromResult<IList<SiteUser>>(Users.Where(u => Claims[u.Id].Any(c => c.Type == claim.Type && c.Value == claim.Value)).ToList());

        public Task RemoveClaimsAsync(SiteUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReplaceClaimAsync(SiteUser user, Claim claim, Claim newClaim, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddToRoleAsync(SiteUser user, string roleName, CancellationToken cancellationToken)
        {
            Roles[user.Id].Add(roleName);
            return Task.CompletedTask;
        }

        public Task<IList<string>> GetRolesAsync(SiteUser user, CancellationToken cancellationToken) =>
            Task.FromResult<IList<string>>(Roles[user.Id].Select(r => r == "WEBADMINS" ? "WebAdmins" : r == "WEBEDITORS" ? "WebEditors" : r).ToList());

        public Task<IList<SiteUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken) =>
            Task.FromResult<IList<SiteUser>>(Users.Where(u => Roles[u.Id].Contains(roleName)).ToList());

        public Task<bool> IsInRoleAsync(SiteUser user, string roleName, CancellationToken cancellationToken) => Task.FromResult(Roles[user.Id].Contains(roleName));

        public Task RemoveFromRoleAsync(SiteUser user, string roleName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SiteUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
            Task.FromResult(Users.FirstOrDefault(u => u.NormalizedEmail == normalizedEmail));

        public Task<string?> GetEmailAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.Email);

        public Task<bool> GetEmailConfirmedAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.EmailConfirmed);

        public Task<string?> GetNormalizedEmailAsync(SiteUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedEmail);

        public Task SetEmailAsync(SiteUser user, string? email, CancellationToken cancellationToken)
        {
            user.Email = email;
            return Task.CompletedTask;
        }

        public Task SetEmailConfirmedAsync(SiteUser user, bool confirmed, CancellationToken cancellationToken)
        {
            user.EmailConfirmed = confirmed;
            return Task.CompletedTask;
        }

        public Task SetNormalizedEmailAsync(SiteUser user, string? normalizedEmail, CancellationToken cancellationToken)
        {
            user.NormalizedEmail = normalizedEmail;
            return Task.CompletedTask;
        }
    }

    private sealed class RoleStore : IQueryableRoleStore<IdentityRole>
    {
        public List<IdentityRole> Roles { get; } = [];

        IQueryable<IdentityRole> IQueryableRoleStore<IdentityRole>.Roles => Roles.AsQueryable();

        public void Dispose()
        {
        }

        public Task<IdentityResult> CreateAsync(IdentityRole role, CancellationToken cancellationToken)
        {
            Roles.Add(role);
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<IdentityResult> DeleteAsync(IdentityRole role, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentityResult> UpdateAsync(IdentityRole role, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);

        public Task<IdentityRole?> FindByIdAsync(string roleId, CancellationToken cancellationToken) => Task.FromResult(Roles.FirstOrDefault(r => r.Id == roleId));

        public Task<IdentityRole?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken) =>
            Task.FromResult(Roles.FirstOrDefault(r => r.NormalizedName == normalizedRoleName));

        public Task<string?> GetNormalizedRoleNameAsync(IdentityRole role, CancellationToken cancellationToken) => Task.FromResult(role.NormalizedName);

        public Task<string> GetRoleIdAsync(IdentityRole role, CancellationToken cancellationToken) => Task.FromResult(role.Id);

        public Task<string?> GetRoleNameAsync(IdentityRole role, CancellationToken cancellationToken) => Task.FromResult(role.Name);

        public Task SetNormalizedRoleNameAsync(IdentityRole role, string? normalizedName, CancellationToken cancellationToken)
        {
            role.NormalizedName = normalizedName;
            return Task.CompletedTask;
        }

        public Task SetRoleNameAsync(IdentityRole role, string? roleName, CancellationToken cancellationToken)
        {
            role.Name = roleName;
            return Task.CompletedTask;
        }
    }
}
