using OptiCli.Agent.Drift;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class DriftEndpoint
{
    /// <remarks>
    /// <c>?compare=model</c> against a local database compares the code with the database's model even though the content type
    /// sync commits it (<see cref="AgentRoutes.DriftOfModel"/>): types and properties only in the database stay, and a sealed
    /// apply's site would find them in production too.
    /// </remarks>
    public static DriftReport Handle(AgentRequest request) =>
        string.Equals(request.Context.Request.Query["compare"].ToString(), "model", StringComparison.Ordinal) && !request.Service<AgentSettings>().SharedDatabase
            ? DriftCheck.CompareModel(request.Context.RequestServices)
            : request.Service<DriftCheck>().Report;
}
