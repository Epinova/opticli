using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Deletes content as the edit UI does, by moving it to the recycle bin (the agent's <c>DELETE /v1/content/{ref}</c>).
/// Nothing here deletes permanently.
/// </summary>
internal static class DeleteOperation
{
    public static MoveResult Run(CmsCall call, string reference)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.ResolveContent(reference);
        var content = MoveOperation.Movable(flow, link);
        if (content.IsDeleted)
        {
            throw AgentException.Conflict($"Content {link.ID} is already in the recycle bin.");
        }

        flow.ThrowIfAborted();
        call.Delete(link);
        return MoveOperation.Result(flow, link, content.ParentLink);
    }
}
