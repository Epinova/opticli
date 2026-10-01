using System.Reflection;
using EPiServer;
using EPiServer.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Agent.Safety;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class PingEndpoint
{
    public static PingResponse Handle(AgentRequest request)
    {
        var settings = request.Service<AgentSettings>();
        // What the CMS's database layer resolves, not what configuration says: those can differ.
        var used = DatabasePin.Resolve(request.Service<IOptions<DataAccessOptions>>().Value);
        var verdict = ConnectionGuard.Check(used?.ConnectionString);

        return new PingResponse
        {
            Protocol = AgentProtocol.Version,
            AgentVersion = AgentInfo.Version,
            CmsVersion = VersionOf(typeof(IContentRepository).Assembly),
            Runtime = Environment.Version.ToString(),
            Environment = request.Service<IWebHostEnvironment>().EnvironmentName,
            Principal = request.Context.User.Identity?.Name ?? "",
            Database = new DatabaseTarget(
                used?.Name ?? settings.ConnectionName,
                verdict.Server,
                verdict.Database,
                verdict.IsLocal,
                settings.PinnedConnection is not null && string.Equals(used?.ConnectionString, settings.PinnedConnection, StringComparison.Ordinal)),
            ProcessId = Environment.ProcessId,
        };
    }

    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}
