using OptiCli.Agent.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

public class AccessRulesTests
{
    private static AccessEntry Role(string name, int mask) => new(name, AccessKinds.Role, AccessLevels.Describe(mask), mask);

    private static AccessEntry User(string name, int mask) => new(name, AccessKinds.User, AccessLevels.Describe(mask), mask);

    private static AccessList Own(params AccessEntry[] entries) => new(false, "10", entries);

    [Fact]
    public void Keeping_an_administering_role_is_fine()
    {
        var before = Own(Role("Administrators", 63), Role("Everyone", 1));
        var after = Own(Role("Administrators", 63), Role("Authenticated", 1));

        Assert.Null(AccessRules.LockOut(before, after));
    }

    [Fact]
    public void Removing_Administer_from_every_role_that_had_it_is_a_lock_out()
    {
        var before = Own(Role("Administrators", 63), Role("WebEditors", 63), Role("Everyone", 1));
        var after = Own(Role("Everyone", 1), Role("WebEditors", AccessLevels.Read | AccessLevels.Edit));

        var message = AccessRules.LockOut(before, after);

        Assert.NotNull(message);
        Assert.Contains("Administrators, WebEditors", message);
    }

    [Fact]
    public void A_user_with_Administer_does_not_count()
    {
        var before = Own(Role("Administrators", 63));
        var after = Own(User("someone@example.com", 63));

        Assert.NotNull(AccessRules.LockOut(before, after));
    }

    [Fact]
    public void Handing_Administer_to_a_standard_admin_role_is_fine()
    {
        var before = Own(Role("SiteOwners", 63));
        var after = Own(Role("CmsAdmins", AccessLevels.Administer));

        Assert.Null(AccessRules.LockOut(before, after));
    }

    [Fact]
    public void An_acl_that_had_no_administering_role_can_stay_that_way()
    {
        Assert.Null(AccessRules.LockOut(Own(Role("Everyone", 1)), Own(Role("Authenticated", 1))));
    }

    [Fact]
    public void Known_roles_match_exactly_first_then_ignoring_case()
    {
        string[] known = ["Everyone", "Authenticated", "authenticated"];

        Assert.Equal("authenticated", AccessRules.KnownRole("authenticated", known));
        Assert.Equal("Everyone", AccessRules.KnownRole("EVERYONE", known));
        Assert.Null(AccessRules.KnownRole("Authenticted", known));
    }

    [Fact]
    public void The_unknown_role_hint_suggests_the_closest_name()
    {
        Assert.StartsWith("Did you mean Authenticated?", AccessRules.UnknownRoleHint("Authenticted", ["Everyone", "Authenticated", "Anonymous"]));
    }

    [Fact]
    public void Same_access_ignores_order_and_name_case()
    {
        var a = Own(Role("Everyone", 1), Role("Administrators", 63));
        var b = Own(Role("administrators", 63), Role("Everyone", 1));

        Assert.True(AccessRules.SameAccess(a, b));
        Assert.False(AccessRules.SameAccess(a, Own(Role("Everyone", 3), Role("Administrators", 63))));
        Assert.False(AccessRules.SameAccess(a, a with { Inherited = true }));
    }
}
