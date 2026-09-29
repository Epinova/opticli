using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Hosting;

/// <summary>
/// Puts the agent at the very front of the pipeline, ahead of the site's own middleware (auth, CMS
/// routing, redirects), as a raw branch that never falls through, so it behaves the same on every site.
/// </summary>
internal sealed class AgentStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Map(AgentProtocol.BasePath, branch => branch.Run(AgentMiddleware.HandleAsync));
        Console.Error.WriteLine($"[opticli] agent {AgentInfo.Version} (protocol v{AgentProtocol.Version}) mapped at {AgentProtocol.BasePath}");
        next(app);
    };
}
