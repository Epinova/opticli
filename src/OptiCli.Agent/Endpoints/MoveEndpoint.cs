using EPiServer.Core;
using EPiServer.Security;
using EPiServer.Web;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary>Move, and delete as a move to the recycle bin. Nothing here deletes permanently.</summary>
internal static class MoveEndpoint
{
    public static MoveResult Move(AgentRequest request, MoveRequest body)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.ResolveContent(request.Argument);
        var destination = flow.Locator.ResolveContent(body.Parent, "parent");
        var content = Movable(request, flow, link);

        var target = flow.Locator.LoadAnyLanguage(destination);
        if (target.ContentLink.CompareToIgnoreWorkID(ContentReference.WasteBasket) || target.IsDeleted)
        {
            throw AgentException.Refused("Moving into the recycle bin is a delete.", $"Use DELETE {AgentRoutes.Delete(link.ID.ToString())} (opticli delete).");
        }
        if (destination.CompareToIgnoreWorkID(link) || flow.Repository.GetAncestors(destination).Any(a => a.ContentLink.CompareToIgnoreWorkID(link)))
        {
            throw AgentException.Usage($"Can't move {link.ID} below itself.");
        }

        flow.Repository.Move(link, destination, AccessLevel.NoAccess, AccessLevel.NoAccess);
        return Result(flow, link, content.ParentLink);
    }

    public static MoveResult Delete(AgentRequest request)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.ResolveContent(request.Argument);
        var content = Movable(request, flow, link);
        if (content.IsDeleted)
        {
            throw AgentException.Conflict($"Content {link.ID} is already in the recycle bin.");
        }

        flow.Repository.MoveToWastebasket(link, AgentProtocol.PrincipalName);
        return Result(flow, link, content.ParentLink);
    }

    /// <summary>
    /// Refuses the structural roots and site start pages, and anything above them: moving those breaks the site even
    /// if it can be undone.
    /// </summary>
    private static IContent Movable(AgentRequest request, WriteFlow flow, ContentReference link)
    {
        var protectedLinks = ProtectedContent.Links(request.Service<ISiteDefinitionRepository>());
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

    private static MoveResult Result(WriteFlow flow, ContentReference link, ContentReference previousParent)
    {
        var moved = flow.Locator.LoadAnyLanguage(link);
        return new MoveResult
        {
            Content = ContentSummaries.Describe(moved, flow.Types),
            PreviousParent = previousParent.ToReferenceWithoutVersion().ToString(),
            Parent = moved.ParentLink.ToReferenceWithoutVersion().ToString(),
        };
    }
}
