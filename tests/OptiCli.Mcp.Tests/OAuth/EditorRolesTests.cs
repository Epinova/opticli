using System.Linq.Expressions;
using System.Security.Claims;
using EPiServer.Security;
using EPiServer.Shell.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tests.OAuth;

/// <summary>
/// Whether an editor's account is active, as <see cref="CmsEditorRoles"/> works it out from the CMS's user provider and
/// its synchronized users: only an account the site itself manages (ASP.NET Identity) can be disabled here.
/// </summary>
public class EditorRolesTests
{
    private static readonly OptiCliMcpOptions Options = new();

    [Fact]
    public async Task Without_a_user_provider_the_site_cannot_say()
    {
        Assert.Null(await Active(users: null, "jane"));
    }

    [Fact]
    public async Task An_approved_identity_user_is_active()
    {
        var users = Identity(new FakeUser("jane"));
        Assert.True(await Active(users, "jane"));
    }

    [Fact]
    public async Task An_identity_user_who_is_not_approved_is_refused()
    {
        var users = Identity(new FakeUser("jane") { IsApproved = false });
        Assert.False(await Active(users, "jane", Synchronized()));
        Assert.Equal(GateRefusal.AccountDisabled, await Refusal(users, Synchronized(), "jane"));
    }

    [Fact]
    public async Task A_locked_out_identity_user_is_refused()
    {
        var users = Identity(new FakeUser("jane") { IsLockedOut = true });
        Assert.False(await Active(users, "jane", Synchronized()));
        Assert.Equal(GateRefusal.AccountDisabled, await Refusal(users, Synchronized(), "jane"));
    }

    [Fact]
    public async Task A_deleted_identity_user_is_refused()
    {
        var users = Identity();
        Assert.False(await Active(users, "jane", Synchronized()));
        Assert.Equal(GateRefusal.AccountDisabled, await Refusal(users, Synchronized(), "jane"));
    }

    [Fact]
    public async Task A_synchronized_user_whose_provider_reports_them_not_approved_is_not_refused()
    {
        // A provider for synchronized users that knows only the name: everyone it returns is "not approved".
        var users = new FakeUserProvider("SynchronizedUsers", new FakeUser("jane@example.com") { IsApproved = false });
        Assert.Null(await Active(users, "jane@example.com", Synchronized("jane@example.com")));
        Assert.Null(await Refusal(users, Synchronized("jane@example.com"), "jane@example.com"));
    }

    [Fact]
    public async Task A_provider_that_finds_nobody_is_no_sign_of_a_deleted_account_unless_it_manages_accounts()
    {
        var users = new FakeUserProvider("SynchronizedUsers");
        Assert.Null(await Active(users, "jane@example.com", Synchronized()));
    }

    [Fact]
    public async Task On_a_site_with_both_logins_an_external_user_missing_from_identity_is_not_refused()
    {
        Assert.Null(await Active(Identity(), "jane@example.com", Synchronized("jane@example.com")));
        // Synchronized wins over a disabled identity account of the same name: the user signs in with the external login.
        Assert.Null(await Active(Identity(new FakeUser("jane@example.com") { IsApproved = false }), "jane@example.com", Synchronized("JANE@example.com")));
    }

    [Fact]
    public async Task Only_an_exact_synchronized_name_counts()
    {
        // The CMS searches by part of the name or e-mail address.
        Assert.False(await Active(Identity(), "jane", Synchronized("jane.doe@example.com", "mary.jane@example.com")));
    }

    [Fact]
    public async Task When_synchronized_users_cannot_be_looked_up_a_missing_identity_user_is_left_to_the_roles()
    {
        Assert.Null(await Active(Identity(), "jane", synchronized: null));
    }

    [Fact]
    public void Identitys_provider_is_known_by_its_name_or_its_type()
    {
        Assert.True(CmsEditorRoles.ManagesAccounts(Identity()));
        Assert.True(CmsEditorRoles.ManagesAccounts(new EPiServer.Cms.UI.AspNetIdentity.ApplicationUserProvider<FakeUser>()));
        Assert.True(CmsEditorRoles.ManagesAccounts(new SiteUserProvider()));
        Assert.False(CmsEditorRoles.ManagesAccounts(new FakeUserProvider("SynchronizedUsers")));
    }

    private static FakeUserProvider Identity(params FakeUser[] users) => new(CmsEditorRoles.AspNetIdentityProviderName, users);

    private static FakeSynchronizedUsers Synchronized(params string[] names) => new(names);

    private static CmsEditorRoles Roles(UIUserProvider? users, SynchronizingRolesSecurityEntityProvider? synchronized)
    {
        var services = new ServiceCollection();
        if (users is not null)
        {
            services.AddSingleton(users);
        }
        if (synchronized is not null)
        {
            services.AddSingleton(synchronized);
        }
        return new CmsEditorRoles(services.BuildServiceProvider(), NullLogger<CmsEditorRoles>.Instance);
    }

    private static Task<bool?> Active(UIUserProvider? users, string name, SynchronizingRolesSecurityEntityProvider? synchronized = null) =>
        Roles(users, synchronized).IsActiveAsync(name, default);

    /// <summary>The whole gate, for an editor with an allowed role.</summary>
    private static Task<GateRefusal?> Refusal(UIUserProvider users, SynchronizingRolesSecurityEntityProvider? synchronized, string name) =>
        new EditorGate(Roles(users, synchronized)).RefusalAsync(EditorGate.Principal(name, ["WebEditors"], "Test"), Options, default);
}

internal sealed class FakeUser(string name) : IUIUser
{
    public FakeUser() : this("")
    {
    }

    public string Username { get; set; } = name;
    public string Email { get; set; } = "";
    public bool IsApproved { get; set; } = true;
    public bool IsLockedOut { get; set; }
    public string PasswordQuestion => "";
    public string ProviderName => "";
    public string Comment { get; set; } = "";
    public DateTime CreationDate => DateTime.UnixEpoch;
    public DateTime? LastLoginDate { get; set; }
    public DateTime? LastLockoutDate => null;
}

/// <summary>A user provider with the users given; <see cref="UIUserProvider.GetUserAsync"/> returns null for anyone else.</summary>
internal class FakeUserProvider(string name, params FakeUser[] users) : UIUserProvider
{
    public override bool Enabled => true;

    public override string Name => name;

    public override Task<IUIUser> GetUserAsync(string username) =>
        Task.FromResult<IUIUser>(users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))!);

    public override Task<int> CountAsync(Expression<Func<IUIUser, bool>>? predicate = null, CancellationToken cancellationToken = default) => Task.FromResult(users.Length);
}

/// <summary>A site's own subclass of ASP.NET Identity's provider, under another name.</summary>
internal sealed class SiteUserProvider : EPiServer.Cms.UI.AspNetIdentity.ApplicationUserProvider<FakeUser>
{
    public override string Name => "SiteUsers";
}

/// <summary>The CMS's synchronized users, searched as the CMS does: by part of the name.</summary>
internal sealed class FakeSynchronizedUsers(string[] names) : SynchronizingRolesSecurityEntityProvider(null!, null!)
{
    public override Task<IEnumerable<SecurityEntity>> SearchAsync(string partOfValue, string claimType) =>
        Task.FromResult(claimType == ClaimTypes.Name
            ? names.Where(n => n.Contains(partOfValue, StringComparison.OrdinalIgnoreCase)).Select(n => new SecurityEntity(n, SecurityEntityType.User))
            : []);
}
