using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using EPiServer.Web;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Reads and replaces one item's ACL through <see cref="IContentSecurityRepository"/> (the agent's
/// <c>POST /v1/content/{ref}/access</c>). Never touches descendants.
/// </summary>
internal static class AccessOperation
{
    public static async Task<AccessResult> RunAsync(CmsCall call, string reference, AccessRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.ResolveContent(reference);
        if (!string.IsNullOrEmpty(link.ProviderName))
        {
            throw AgentException.Usage($"{link} is content from a content provider; its access rights are the provider's.");
        }
        if (ProtectedContent.Contains(ProtectedContent.Links(call.Service<ISiteDefinitionRepository>()), link))
        {
            throw AgentException.Refused(
                $"Content {link.ID} is the root, the recycle bin, the global block folder, a start page or an asset root; opticli won't change its access rights.",
                "Change the section below it instead: children that inherit follow.");
        }

        var grants = body.Grant ?? new Dictionary<string, string>();
        var userGrants = body.GrantUsers ?? new Dictionary<string, string>();
        var revokes = body.Revoke ?? [];
        var changes = grants.Count + userGrants.Count + revokes.Count > 0;
        if (body.Inherit && (changes || body.BreakInheritance))
        {
            throw AgentException.Usage("inherit drops the item's own entries; it can't be combined with grants, revokes or breakInheritance.");
        }
        if (!changes && !body.Inherit && !body.BreakInheritance && !body.DryRun)
        {
            throw AgentException.Usage("Nothing to change: give grant, grantUsers, revoke, breakInheritance or inherit.");
        }

        // The CMS doesn't check who saves an ACL; the edit UI only lets those with Administer change one.
        call.RequireAccess(flow.Locator.LoadAnyLanguage(link), AccessLevel.Administer);

        var security = call.Service<IContentSecurityRepository>();
        var current = (AccessControlList)security.Get(link);
        var before = Effective(flow, security, link);
        if (current.IsInherited && changes && !body.BreakInheritance)
        {
            throw AgentException.Usage(
                $"Content {link.ID} inherits its access rights from {before.From}.",
                $"Pass breakInheritance (--break-inheritance) to give it its own, starting from the inherited entries, or change {before.From} instead.");
        }

        var writable = (AccessControlList)((IReadOnly)current).CreateWritableClone();
        var issues = new List<ValidationIssue>();
        if (body.Inherit)
        {
            if (!current.IsInherited)
            {
                writable.ClearEntries();
                writable.IsInherited = true;
            }
        }
        else
        {
            if (current.IsInherited && body.BreakInheritance)
            {
                var inherited = writable.Entries.ToList();
                writable.IsInherited = false;
                writable.ClearEntries();
                foreach (var entry in inherited)
                {
                    writable.Add(new AccessControlEntry(entry.Name, entry.Access, entry.EntityType));
                }
            }
            foreach (var name in revokes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Entry(writable, name) is { } existing)
                {
                    writable.Remove(existing.Name);
                }
                else
                {
                    issues.Add(new ValidationIssue(null, $"'{name}' has no entry to revoke.", "warning"));
                }
            }
            var knownRoles = grants.Count > 0 ? await KnownRolesAsync(call, before) : [];
            foreach (var (name, levels) in grants)
            {
                var role = AccessRules.KnownRole(name, knownRoles) ?? await SearchRoleAsync(call, name);
                if (role is null && !body.AllowUnknownRole)
                {
                    throw AgentException.Usage($"Unknown role '{name}'.", AccessRules.UnknownRoleHint(name, knownRoles));
                }
                Grant(writable, role ?? name, levels, SecurityEntityType.Role);
            }
            foreach (var (name, levels) in userGrants)
            {
                Grant(writable, name, levels, SecurityEntityType.User);
            }
        }

        var after = writable.IsInherited
            ? current.IsInherited ? before : Effective(flow, security, flow.Locator.LoadAnyLanguage(link).ParentLink) with { Inherited = true }
            : Own(link, writable);
        if (AccessRules.LockOut(before, after) is { } lockOut)
        {
            throw AgentException.Refused(lockOut, $"Keep Administer for one of {string.Join(", ", AccessRules.AdminRoles)} (or a role that has it now).");
        }

        var saved = false;
        if (!body.DryRun && !AccessRules.SameAccess(before, after))
        {
            flow.ThrowIfAborted();
            security.Save(link, (IContentSecurityDescriptor)writable, SecuritySaveType.Replace);
            after = Effective(flow, security, link);
            saved = true;
        }
        return new AccessResult
        {
            Content = ContentSummaries.Describe(flow.Locator.LoadUnchecked(link), flow.Types),
            Before = before,
            After = after,
            Saved = saved,
            DryRun = body.DryRun,
            Validation = issues.Count > 0 ? issues : null,
        };
    }

    private static void Grant(AccessControlList acl, string name, string levels, SecurityEntityType type)
    {
        if (!AccessLevels.TryParse(levels, out var mask, out var error))
        {
            throw AgentException.Usage($"{name}: {error}");
        }
        if (Entry(acl, name) is { } existing)
        {
            // The ACL holds one entry per name (case-insensitive), so a grant would silently turn a role into a user.
            if (existing.EntityType != type)
            {
                var (was, wanted) = (AccessKinds.From((int)existing.EntityType), AccessKinds.From((int)type));
                throw AgentException.Usage(
                    $"'{existing.Name}' has a {was} entry; an item can't have a {wanted} entry of the same name as well.",
                    $"Revoke it in the same change (revokes apply before grants) to replace it with the {wanted}.");
            }
            acl.Remove(existing.Name);
        }
        acl.Add(new AccessControlEntry(name, (AccessLevel)mask, type));
    }

    private static AccessControlEntry? Entry(AccessControlList acl, string name) =>
        acl.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.Ordinal))
        ?? acl.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The ACL that applies to <paramref name="link"/>: its own, or that of the nearest ancestor with one.</summary>
    private static AccessList Effective(WriteFlow flow, IContentSecurityRepository security, ContentReference link)
    {
        var acl = (AccessControlList)security.Get(link);
        if (!acl.IsInherited)
        {
            return Own(link, acl);
        }
        // Ancestors unchecked: only the id of the one the rights come from is shown, as the edit UI does.
        for (var parent = flow.Locator.LoadUnchecked(link).ParentLink; !ContentReference.IsNullOrEmpty(parent); parent = flow.Locator.LoadUnchecked(parent).ParentLink)
        {
            if (!((AccessControlList)security.Get(parent)).IsInherited)
            {
                return new AccessList(true, parent.ToReferenceWithoutVersion().ToString(), Entries(acl));
            }
        }
        return new AccessList(true, null, Entries(acl));
    }

    private static AccessList Own(ContentReference link, AccessControlList acl) =>
        new(false, link.ToReferenceWithoutVersion().ToString(), Entries(acl));

    private static IReadOnlyList<AccessEntry> Entries(AccessControlList acl) =>
        AccessEntry.Sorted(acl.Entries.Select(e => new AccessEntry(
            e.Name, AccessKinds.From((int)e.EntityType), AccessLevels.Describe((int)e.Access), (int)e.Access)));

    /// <summary>Virtual roles (Everyone, Authenticated, CmsAdmins, ...) and the roles already on the item.</summary>
    private static async Task<List<string>> KnownRolesAsync(CmsCall call, AccessList current)
    {
        var known = call.Service<IVirtualRoleRepository>().GetAllRoles().ToList();
        known.AddRange(current.Entries.Where(e => e.Kind == AccessKinds.Role).Select(e => e.Name));
        try
        {
            if (call.OptionalService<SecurityEntityProvider>() is { } provider)
            {
                known.AddRange((await provider.SearchRolesAsync(null, 0, 500)).Roles.Select(r => r.Name));
            }
        }
        catch (Exception)
        {
            // Some providers can't list roles (e.g. external identity providers); virtual roles still count.
        }
        return known.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A role the site's security entity provider knows by exactly this name (case-insensitive).</summary>
    private static async Task<string?> SearchRoleAsync(CmsCall call, string name)
    {
        try
        {
            return call.OptionalService<SecurityEntityProvider>() is { } provider
                ? (await provider.SearchRolesAsync(name)).Select(r => r.Name).FirstOrDefault(r => r.Equals(name, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
