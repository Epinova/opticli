using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>The checks on an ACL change that don't need the CMS: lock-out, role names, whether anything changed.</summary>
internal static class AccessRules
{
    /// <summary>Roles that administer a CMS 12 site by default.</summary>
    public static readonly IReadOnlyList<string> AdminRoles = ["Administrators", "WebAdmins", "CmsAdmins"];

    /// <summary>
    /// A change must leave Administer with at least one role that had it before, or with one of <see cref="AdminRoles"/>:
    /// otherwise nobody could change the item's access rights back in the edit UI.
    /// </summary>
    /// <returns>Null when the change keeps an administering role; else why it doesn't.</returns>
    public static string? LockOut(AccessList before, AccessList after)
    {
        var administering = Administering(before).ToList();
        if (administering.Count == 0)
        {
            return null;
        }
        var keep = AdminRoles.Concat(administering).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Administering(after).Any(keep.Contains))
        {
            return null;
        }
        return $"After this change no role keeps Administer (before: {string.Join(", ", administering)}), so nobody could change the access rights back.";
    }

    /// <summary>The known role <paramref name="name"/> refers to, in its own casing; null when it isn't known.</summary>
    public static string? KnownRole(string name, IEnumerable<string> known) =>
        known.FirstOrDefault(k => k.Equals(name, StringComparison.Ordinal))
        ?? known.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string UnknownRoleHint(string name, IEnumerable<string> known)
    {
        var closest = Suggestions.Closest(name, known);
        var didYouMean = closest.Count > 0 ? $"Did you mean {string.Join(" or ", closest)}? " : "";
        return $"{didYouMean}Pass allowUnknownRole (--allow-unknown-role) for a role the site only learns about later, e.g. one an identity provider creates on first sign-in.";
    }

    public static bool SameAccess(AccessList a, AccessList b) =>
        a.Inherited == b.Inherited && Key(a) == Key(b);

    private static IEnumerable<string> Administering(AccessList list) =>
        list.Entries.Where(e => e.Kind == AccessKinds.Role && (e.Mask & AccessLevels.Administer) != 0).Select(e => e.Name);

    private static string Key(AccessList list) =>
        string.Join("|", AccessEntry.Sorted(list.Entries).Select(e => $"{e.Name.ToUpperInvariant()}:{e.Kind}:{e.Mask}"));
}
