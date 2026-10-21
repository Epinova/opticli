using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Hosting;

/// <summary>
/// Puts the agent at the very front of the pipeline, ahead of the site's own middleware (auth, CMS
/// routing, redirects), as a raw branch that never falls through, so it behaves the same on every site.
/// </summary>
internal sealed class AgentStartupFilter(AgentSettings settings) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var pipeline = Pipeline(next, StdinSecrets.Expected, AgentMiddleware.HandleAsync);
        if (!settings.Ide)
        {
            return pipeline;
        }
        return app =>
        {
            IdeRun.Register(app, settings);
            pipeline(app);
        };
    }

    /// <param name="sealedRun">
    /// The site runs for a sealed apply to production (<see cref="StdinSecrets.Expected"/>): it then serves the agent only.
    /// Its own pages, the CMS's edit UI and login, and every other endpoint of the site answer 404, so no other process on
    /// this machine reaches the site while it runs in Development with production's write login (its local logins, admin
    /// registration, Razor views compiled at runtime from files the seal doesn't cover).
    /// </param>
    internal static Action<IApplicationBuilder> Pipeline(Action<IApplicationBuilder> next, bool sealedRun, RequestDelegate agent) => app =>
    {
        app.Map(AgentProtocol.BasePath, branch => branch.Run(agent));
        Console.Error.WriteLine($"[opticli] agent {AgentInfo.Version} (protocol v{AgentProtocol.Version}) mapped at {AgentProtocol.BasePath}");
        if (sealedRun)
        {
            // The configuration and the agent's settings have read them by now.
            StdinSecrets.Forget();
            app.Use((HttpContext context, RequestDelegate _) =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
            Console.Error.WriteLine($"[opticli] sealed apply: the site serves only {AgentProtocol.BasePath} for this run");
        }
        // The site's own pipeline is still built (its Configure runs), so the CMS is set up as usual.
        next(app);
    };
}
