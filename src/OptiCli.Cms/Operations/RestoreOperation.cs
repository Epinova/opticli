using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Brings content back out of the recycle bin (the agent's <c>POST /v1/content/{ref}/restore</c>), as the edit UI's
/// Restore does: below the parent it had before it was deleted, or below the parent the request names.
/// </summary>
/// <remarks>
/// <para>The CMS's own <c>ParentRestoreService</c> saves every moved item's previous parent in the Dynamic Data Store
/// (<see cref="IParentRestoreRepository"/>), and its <c>Restore</c> moves the item back there with
/// <c>AccessLevel.NoAccess</c>. This does the same move, through <see cref="CmsCall.Move"/>, after the checks the edit UI
/// makes before it offers Restore (the parent exists and isn't in the recycle bin) and the ones <c>move</c> makes (the
/// type is allowed there). The change log (<c>tblActivityLog</c>) isn't used, so truncating it changes nothing here.</para>
/// <para>Here for the developer only: an editor restores in the CMS edit UI (<see cref="CmsCall.MayRestore"/>), and the
/// MCP module has no tool for it.</para>
/// </remarks>
internal static class RestoreOperation
{
    public static RestoreResult Run(CmsCall call, string reference, RestoreRequest body)
    {
        var locator = new ContentLocator(call);
        var link = locator.ResolveContent(reference);
        var content = locator.LoadAnyLanguage(link);
        if (!call.MayRestore)
        {
            throw AgentException.Refused(
                $"Content {link.ID} ('{content.Name}') can't be restored here.",
                "Ask the user to restore it in the CMS edit UI (the recycle bin), where they see what comes back. Nothing was changed.");
        }
        if (!content.IsDeleted)
        {
            throw AgentException.Conflict($"Content {link.ID} ('{content.Name}') is not in the recycle bin.", "`opticli trash` lists what is.");
        }
        if (!content.ParentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket))
        {
            var deleted = DeletedRoot(locator, content);
            throw AgentException.Usage(
                $"Content {link.ID} ('{content.Name}') is in the recycle bin because {deleted.ContentLink.ID} ('{deleted.Name}') above it was deleted.",
                $"Restore {deleted.ContentLink.ID}, which brings {link.ID} back with it (`opticli restore {deleted.ContentLink.ID}`); then move {link.ID} if it should go elsewhere.");
        }

        var stored = call.Service<IParentRestoreRepository>().GetParentLink(link.ToReferenceWithoutVersion());
        var storedParent = ContentReference.IsNullOrEmpty(stored) ? null : stored.ToReferenceWithoutVersion();
        ContentReference destination;
        IContent target;
        if (body.Parent is not null)
        {
            destination = locator.ResolveContent(body.Parent, "parent");
            target = locator.LoadAnyLanguage(destination);
        }
        else if (storedParent is null)
        {
            throw AgentException.Usage(
                $"The CMS has no record of where {link.ID} ('{content.Name}') was before it was deleted.",
                "Give the parent to restore it below: --to <parent> (in a plan, \"to\").");
        }
        else
        {
            destination = storedParent;
            if (!locator.Repository.TryGet<IContent>(destination, AnyLanguage, out target))
            {
                throw AgentException.Conflict(
                    $"{link.ID} ('{content.Name}') was below {destination.ID}, which no longer exists (it was deleted for good).",
                    "Give another parent: --to <parent>.");
            }
        }

        if (target.ContentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket))
        {
            throw AgentException.Usage($"{link.ID} is in the recycle bin already; give a parent outside it.");
        }
        if (target.IsDeleted)
        {
            var deleted = DeletedRoot(locator, target);
            var also = deleted.ContentLink.CompareToIgnoreWorkID(target.ContentLink) ? "" : $" (with {deleted.ContentLink.ID}, '{deleted.Name}', above it)";
            throw AgentException.Conflict(
                $"{(body.Parent is null ? "The parent it had" : "The parent")}, {destination.ID} ('{target.Name}'), is in the recycle bin too{also}.",
                $"Restore {deleted.ContentLink.ID} first (`opticli restore {deleted.ContentLink.ID}`), or give a parent outside the recycle bin: --to <parent>.");
        }
        if (destination.CompareToIgnoreWorkID(link) || locator.Repository.GetAncestors(destination).Any(a => a.ContentLink.CompareToIgnoreWorkID(link)))
        {
            throw AgentException.Usage($"Can't restore {link.ID} below itself.");
        }
        // As for a move: content goes back only where it could be created.
        var types = call.Service<IContentTypeRepository>();
        if (types.Load(content.ContentTypeID) is { } type && types.Load(target.ContentTypeID) is { } targetType
            && CreateOperation.Placement(call, type, targetType, target.ContentLink.ID, creating: false) is { Count: > 0 } issues)
        {
            throw AgentException.Invalid(issues, "Restore it below a parent whose type allows it: --to <parent>. Nothing was changed.");
        }

        if (body.DryRun)
        {
            return Result(content, destination, storedParent, types, restored: false);
        }
        if (call.Aborted.IsCancellationRequested)
        {
            throw new AgentException(AgentErrorCodes.Internal, "The caller stopped waiting, so nothing was restored.", "the CLI timed out or was interrupted");
        }
        call.Move(link, destination);
        return Result(locator.LoadUnchecked(link), destination, storedParent, types, restored: true);
    }

    /// <summary>The item directly in the recycle bin that <paramref name="content"/> is in (itself, or an ancestor).</summary>
    private static IContent DeletedRoot(ContentLocator locator, IContent content) =>
        content.ParentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket)
            ? content
            : locator.Repository.GetAncestors(content.ContentLink).FirstOrDefault(a => a.ParentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket)) ?? content;

    private static RestoreResult Result(IContent content, ContentReference parent, ContentReference? stored, IContentTypeRepository types, bool restored) => new()
    {
        Content = ContentSummaries.Describe(content, types),
        Parent = parent.ToReferenceWithoutVersion().ToString(),
        PreviousParent = ContentReference.WasteBasket.ToReferenceWithoutVersion().ToString(),
        StoredParent = stored?.ToString(),
        Restored = restored,
        DryRun = !restored,
    };

    private static LoaderOptions AnyLanguage => new() { LanguageLoaderOption.FallbackWithMaster() };
}
