using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary>A sealed apply's event and blob providers (<see cref="SealedAzure"/>): only in a sealed run that was given them.</summary>
internal static class SealedEndpoint
{
    public static SealedStatus Status(AgentRequest request)
    {
        var settings = Sealed(request);
        return SealedAzure.Status(request.Context.RequestServices, settings);
    }

    public static async Task<EventsFlushResult> FlushAsync(AgentRequest request)
    {
        if (Sealed(request).Events is null)
        {
            throw AgentException.NotFound("This run has no event bus to flush.");
        }
        return await SealedAzure.FlushAsync(request.Context.RequestServices, request.Context.RequestAborted);
    }

    private static AgentSettings Sealed(AgentRequest request) =>
        StdinSecrets.Expected ? request.Service<AgentSettings>() : throw AgentException.NotFound("Only a sealed apply's site has these routes.");
}
