using EPiServer;
using EPiServer.Core;
using EPiServer.DataAccess;
using EPiServer.Web;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Moves content below another parent (the agent's <c>POST /v1/content/{ref}/move</c>); never into the recycle bin,
/// which is <see cref="DeleteOperation"/>.
/// </summary>
internal static class MoveOperation
{
    public static MoveResult Run(CmsCall call, string reference, MoveRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.ResolveContent(reference);
        var destination = flow.Locator.ResolveContent(body.Parent, "parent");
        var content = Movable(flow, link);
        if (content.IsDeleted && !call.MayRestore)
        {
            throw AgentException.Refused(
                $"Content {link.ID} ('{content.Name}') is in the recycle bin; moving it out would restore it, published versions and all.",
                "Ask the user to restore it in the CMS edit UI (the recycle bin), where they see what comes back. Nothing was changed.");
        }

        var target = flow.Locator.LoadAnyLanguage(destination);
        if (target.ContentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket) || target.IsDeleted)
        {
            throw AgentException.Refused("Moving into the recycle bin is a delete.", flow.Call.ForCaller(
                $"Use DELETE {AgentRoutes.Delete(link.ID.ToString())} (opticli delete).",
                "Delete it instead (delete_content, where the site allows it), which moves it to the recycle bin."));
        }
        if (destination.CompareToIgnoreWorkID(link) || flow.Repository.GetAncestors(destination).Any(a => a.ContentLink.CompareToIgnoreWorkID(link)))
        {
            throw AgentException.Usage($"Can't move {link.ID} below itself.");
        }
        // The same rule as for new content: the edit UI doesn't let content be moved where it couldn't be created.
        if (flow.Types.Load(content.ContentTypeID) is { } type && flow.Types.Load(target.ContentTypeID) is { } targetType
            && CreateOperation.Placement(call, type, targetType, target.ContentLink.ID, creating: false) is { Count: > 0 } issues)
        {
            throw AgentException.Invalid(issues);
        }
        // Also for a dry run, so the assistant learns before it tries.
        if (call.ChecksLiveMoves && MayBeLive(flow, link, content))
        {
            call.RequirePublishing(SaveAction.Publish);
            if (Approvals.MoveChangesSequence(call, link, destination))
            {
                throw new AgentException(AgentErrorCodes.Refused,
                    $"Content {link.ID} ('{content.Name}') may be live, and another approval sequence applies below {destination.ID} than where it is now: moved there, what of it is published would skip that sequence's reviewers.",
                    "Ask the user to move it in the CMS edit UI. Nothing was changed.")
                {
                    Reason = AgentErrorReasons.ApprovalSequence,
                };
            }
        }
        if (body.DryRun)
        {
            return new MoveResult
            {
                Content = ContentSummaries.Describe(content, flow.Types),
                PreviousParent = content.ParentLink.ToReferenceWithoutVersion().ToString(),
                Parent = destination.ToReferenceWithoutVersion().ToString(),
            };
        }

        flow.ThrowIfAborted();
        call.Move(link, destination);
        return Result(flow, link, content.ParentLink);
    }

    /// <summary>
    /// Refuses the structural roots and site start pages, and anything above them: moving those breaks the site even
    /// if it can be undone.
    /// </summary>
    internal static IContent Movable(WriteFlow flow, ContentReference link)
    {
        var protectedLinks = ProtectedContent.Links(flow.Call);
        if (ProtectedContent.Contains(protectedLinks, link))
        {
            throw AgentException.Refused($"Content {link.ID} is a site root, start page, asset root or the recycle bin; opticli won't move or delete it.");
        }
        var below = protectedLinks.FirstOrDefault(p => flow.Repository.GetAncestors(p).Any(a => a.ContentLink.CompareToIgnoreWorkID(link)));
        if (below is not null)
        {
            throw AgentException.Refused($"Content {link.ID} contains {below.ID}, a site root, start page or asset root; opticli won't move or delete it.");
        }
        return flow.Locator.LoadAnyLanguage(link);
    }

    /// <summary>
    /// Whether moving <paramref name="content"/> may change what visitors see: it has a published version in any language,
    /// or it has children (any of which may be live, whatever the editor can read), or it has no versions at all and is
    /// live as it is. An empty folder shows visitors nothing.
    /// </summary>
    private static bool MayBeLive(WriteFlow flow, ContentReference link, IContent content) =>
        content is not (IVersionable or ContentFolder)
        || (content is IVersionable && flow.Versions.List(link.ToReferenceWithoutVersion()).Any(v => v.Status == VersionStatus.Published))
        || flow.Call.Service<IContentLoader>().GetChildren<IContent>(link.ToReferenceWithoutVersion(), new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }, 0, 1).Any();

    internal static MoveResult Result(WriteFlow flow, ContentReference link, ContentReference previousParent)
    {
        var moved = flow.Locator.LoadUnchecked(link);
        return new MoveResult
        {
            Content = ContentSummaries.Describe(moved, flow.Types),
            PreviousParent = previousParent.ToReferenceWithoutVersion().ToString(),
            Parent = moved.ParentLink.ToReferenceWithoutVersion().ToString(),
        };
    }
}
