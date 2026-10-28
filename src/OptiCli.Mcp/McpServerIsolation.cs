using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp;

/// <summary>
/// Keeps the module's MCP server apart from any other MCP server the site runs: Optimizely's own (CMS 13's
/// <c>Optimizely.Cms.Opal.Tools</c>), or any package built on the same MCP SDK. The SDK has one set of server options
/// for the whole application, filled from every <c>AddMcpServer()</c>: without this, every MCP endpoint would serve every
/// server's tools, prompts, resources, filters, handlers and instructions. The module's tools would run at the other
/// server's endpoint, under its sign-in (Optimizely's client secret, which signs in as CmsAdmins), and the other's at
/// the module's, for an editor's assistant, past the module's gates (scopes, AllowPublish, the edit UI's rules).
/// </summary>
/// <remarks>
/// The SDK makes each request's (or session's) server from those options, then hands it to
/// <see cref="HttpServerTransportOptions.ConfigureSessionOptions"/> with the request: the module's endpoint
/// (<see cref="ModuleEndpoint"/>) gets exactly the module's server, whatever else is registered, and every other endpoint
/// its own, without the module's tools. A site with only the module serves what it always did.
/// </remarks>
internal static class McpServerIsolation
{
    /// <summary>Metadata on the module's MCP endpoint (<see cref="OptiCliMcpExtensions.MapOptiCliMcp"/>).</summary>
    internal sealed class ModuleEndpoint;

    /// <summary>The module's server name, as MCP's <c>serverInfo</c>.</summary>
    internal const string ServerName = "opticli";

    public static void Register(IServiceCollection services) =>
        // After every Configure: another server's own hook runs on its endpoints, never on the module's.
        services.PostConfigure<HttpServerTransportOptions>(transport =>
        {
            var others = transport.ConfigureSessionOptions;
            transport.ConfigureSessionOptions = async (context, server, cancellationToken) =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<ModuleEndpoint>() is not null)
                {
                    Module(context.RequestServices, server);
                    return;
                }
                if (others is not null)
                {
                    await others(context, server, cancellationToken);
                }
                WithoutModule(server);
            };
        });

    /// <summary>The module's server: its tools (no delete tool unless the site allows deleting), filters, name and instructions, nothing else.</summary>
    internal static void Module(IServiceProvider services, McpServerOptions server)
    {
        var site = services.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value;
        server.ServerInfo = new() { Name = ServerName, Version = OptiCliMcpExtensions.Version };
        server.ServerInstructions = McpInstructions.For(site);
        server.Capabilities = null;
        server.Handlers = new();
        // Another server's handlers for any request method (experimental in the SDK, but what it would run): none here.
#pragma warning disable MCPEXP002
        server.RequestHandlers = null;
#pragma warning restore MCPEXP002
        server.Filters = new();
        // The audit outside, so it records the error results the inner filter makes of refusals.
        server.Filters.Request.CallToolFilters.Add(ToolAudit.Filter);
        server.Filters.Request.CallToolFilters.Add(ToolErrors.Filter);
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in services.GetServices<McpServerTool>().Where(IsModules))
        {
            // What the site allows is only known once its options are: whether deleting is offered at all (the tool also
            // checks, should it be called regardless).
            if (site.AllowDelete || tool.ProtocolTool.Name != DeleteTool.ToolName)
            {
                tools.TryAdd(tool);
            }
        }
        server.ToolCollection = tools;
        server.PromptCollection = null;
        server.ResourceCollection = null;
    }

    /// <summary>Another endpoint's server, without the module's tools.</summary>
    internal static void WithoutModule(McpServerOptions server)
    {
        if (server.ToolCollection is not { } tools)
        {
            return;
        }
        foreach (var tool in tools.Where(IsModules).ToList())
        {
            tools.Remove(tool);
        }
        if (tools.IsEmpty)
        {
            server.ToolCollection = null;
        }
    }

    /// <summary>Whether <paramref name="tool"/> is one of the module's: made from a method of this assembly (the SDK's metadata names it).</summary>
    internal static bool IsModules(McpServerTool tool) =>
        tool.Metadata.OfType<MethodInfo>().Any(m => m.DeclaringType?.Assembly == typeof(McpServerIsolation).Assembly);
}
