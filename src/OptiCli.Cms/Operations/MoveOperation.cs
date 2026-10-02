using EPiServer.Core;
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

        var target = flow.Locator.LoadAnyLanguage(destination);
        if (target.ContentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket) || target.IsDeleted)
        {
            throw AgentException.Refused("Moving into the recycle bin is a delete.", $"Use DELETE {AgentRoutes.Delete(link.ID.ToString())} (opticli delete).");
        }
        if (destination.CompareToIgnoreWorkID(link) || flow.Repository.GetAncestors(destination).Any(a => a.ContentLink.CompareToIgnoreWorkID(link)))
        {
            throw AgentException.Usage($"Can't move {link.ID} below itself.");
        }
        // The same rule as for new content: the edit UI doesn't let content be moved where it couldn't be created.
        if (flow.Types.Load(content.ContentTypeID) is { } type && flow.Types.Load(target.ContentTypeID) is { } targetType
            && CreateOperation.Placement(call, type, targetType, target.ContentLink.ID) is { Count: > 0 } issues)
        {
            throw AgentException.Invalid(issues);
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
        var protectedLinks = ProtectedContent.Links(flow.Call.Service<ISiteDefinitionRepository>());
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
