using OptiCli.Agent.Hosting;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Http;

/// <summary>
/// The site of a sealed apply to production (<see cref="StdinSecrets.Sealed"/>) runs only what a sealed plan can do: reads,
/// and creating, changing, translating, publishing, moving, restoring and uploading content, never with someone else's
/// draft. The CLI checks the plan before it seals and again before it runs (<c>SealedPlan</c>); this is the same limit
/// where the writes happen, so nothing else that reaches the run's agent gets more.
/// </summary>
internal static class SealedRunGuard
{
    /// <summary>The reason the CLI gives for a step a sealed plan doesn't have (<c>SealedPlan.Reason</c>), for the same limit.</summary>
    public const string Reason = "sealedPlan";

    /// <summary>The writes a sealed run makes; every other write endpoint is refused.</summary>
    internal static readonly IReadOnlySet<AgentEndpoint> Writes = new HashSet<AgentEndpoint>
    {
        AgentEndpoint.Create,
        AgentEndpoint.Upload,
        AgentEndpoint.Draft,
        AgentEndpoint.Languages,
        AgentEndpoint.Publish,
        AgentEndpoint.Move,
        AgentEndpoint.Restore,
    };

    /// <exception cref="AgentException">The endpoint is a write a sealed run doesn't make (<c>refused</c>).</exception>
    public static void Check(RouteMatch route, AgentMiddleware.EndpointHandler handler)
    {
        if (!handler.Gated)
        {
            return;
        }
        if (route.Extension is not null || !Writes.Contains(route.Endpoint))
        {
            throw Refused(route.Extension is not null ? "changes Commerce data" : $"is a {route.Endpoint} write");
        }
    }

    /// <exception cref="AgentException">The body puts someone else's unpublished changes live or saves over them.</exception>
    public static void CheckBody(object body)
    {
        var includeDraft = body switch
        {
            DraftRequest draft => draft.IncludeDraft,
            CreateRequest create => create.IncludeDraft,
            UploadRequest upload => upload.IncludeDraft,
            PublishRequest publish => publish.IncludeDraft,
            _ => false,
        };
        if (includeDraft)
        {
            throw Refused("has includeDraft, which would put someone else's unpublished changes live");
        }
    }

    private static AgentException Refused(string what) =>
        new(AgentErrorCodes.Refused,
            $"This site runs for a sealed apply to production, and the request {what}: a sealed apply doesn't.",
            "A sealed apply creates, changes, moves, translates and publishes content by GUID, and nothing else.")
        {
            Reason = Reason,
        };
}
