using OptiCli.Agent.Drift;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class DriftEndpoint
{
    public static DriftReport Handle(AgentRequest request) => request.Service<DriftCheck>().Report;
}
