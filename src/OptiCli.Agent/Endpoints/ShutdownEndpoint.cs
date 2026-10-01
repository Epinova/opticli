using Microsoft.Extensions.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary>
/// <c>serve --stop</c>'s graceful stop on every OS (Windows has no SIGTERM): the host shuts down as it would on Ctrl+C.
/// </summary>
internal static class ShutdownEndpoint
{
    public static ShutdownResponse Handle(AgentRequest request)
    {
        var lifetime = request.Service<IHostApplicationLifetime>();
        // After the response is sent, so the caller gets its answer before Kestrel stops accepting requests.
        request.Context.Response.OnCompleted(() =>
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
        return new ShutdownResponse(Stopping: true);
    }
}
