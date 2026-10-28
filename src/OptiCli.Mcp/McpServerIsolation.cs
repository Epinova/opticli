using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp;

/// <summary>
/// Keeps the module's MCP server apart from any other MCP server the site runs: Optimizely's own (CMS 13's
/// <c>Optimizely.Cms.Opal.Tools</c>), the site's own, or any package built on the same MCP SDK. The SDK has one set of
/// server options for the whole application, filled from every <c>AddMcpServer()</c>: without this, every MCP endpoint
/// would serve every server's tools, prompts, resources, filters, handlers and instructions. The module's tools would run
/// at the other server's endpoint, under its sign-in (Optimizely's client secret, which signs in as CmsAdmins), and the
/// other's at the module's, for an editor's assistant, past the module's gates (scopes, AllowPublish, the edit UI's
/// rules).
/// </summary>
/// <remarks>
/// <para>
/// The SDK makes each request's (or session's) server from those options, then hands it to
/// <see cref="HttpServerTransportOptions.ConfigureSessionOptions"/> with the request: the module's endpoint
/// (<see cref="ModuleEndpoint"/>) gets exactly the module's server, whatever else is registered, and every other endpoint
/// its own, without the module's tools. A site with only the module serves what it always did.
/// </para>
/// <para>
/// The SDK's transport settings and its session stores are the application's too. The module asks for stateless HTTP,
/// but a site whose own server sets <c>Stateless = false</c> (and <c>EnableLegacySse</c>) makes the module's endpoint
/// stateful as well, maps legacy SSE under it, and the SDK finds a session by its ID alone, at any endpoint. So the
/// module's endpoint serves the same whatever the site set: Streamable HTTP POSTs (and a DELETE that ends one of its own
/// sessions), never a GET stream or legacy SSE (<see cref="Guard"/>); it takes only the sessions it made, each only from
/// the connection that made it (<see cref="McpSessions"/>); every session the SDK makes for it is bound to that
/// connection, by the claim the SDK compares (<see cref="TokenService.SessionSubject"/>) and by the module's own check
/// of every message (<see cref="BoundTo"/>). And should something replace the module's hook after it (a later
/// <c>PostConfigure</c>), the endpoint refuses to serve rather than serve without it (<see cref="Installed"/>).
/// </para>
/// </remarks>
internal static class McpServerIsolation
{
    /// <summary>Metadata on the module's MCP endpoint (<see cref="OptiCliMcpExtensions.MapOptiCliMcp"/>).</summary>
    internal sealed class ModuleEndpoint;

    /// <summary>The module's server name, as MCP's <c>serverInfo</c>.</summary>
    internal const string ServerName = "opticli";

    /// <summary>The Streamable HTTP session header.</summary>
    internal const string SessionHeader = "Mcp-Session-Id";

    /// <summary>The server options <see cref="Module"/> made: the servers that may run the module's tools.</summary>
    private static readonly ConditionalWeakTable<McpServerOptions, object> Made = new();

    private static readonly object Mark = new();

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<McpSessions>();
        // After every Configure: another server's own hook runs on its endpoints, never on the module's.
        services.PostConfigure<HttpServerTransportOptions>(transport => transport.ConfigureSessionOptions = new Hook(transport.ConfigureSessionOptions).ConfigureAsync);
    }

    /// <summary>The module's session hook, in place of the one it found: another server's, which it runs on every other endpoint.</summary>
    private sealed class Hook(Func<HttpContext, McpServerOptions, CancellationToken, Task>? others)
    {
        public async Task ConfigureAsync(HttpContext context, McpServerOptions server, CancellationToken cancellationToken)
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<ModuleEndpoint>() is not null)
            {
                Module(context, server);
                return;
            }
            if (others is not null)
            {
                await others(context, server, cancellationToken);
            }
            WithoutModule(server);
        }
    }

    /// <summary>
    /// Whether the SDK runs the module's hook: not when something replaced it after the module (a <c>PostConfigure</c>
    /// registered after <c>AddOptiCliMcp()</c>, or a change at run time), even one that calls the module's in turn, since
    /// what it does around it can't be known.
    /// </summary>
    internal static bool Installed(HttpServerTransportOptions transport) => transport.ConfigureSessionOptions?.Target is Hook;

    /// <summary>Why the module's endpoint refuses every request when <see cref="Installed"/> is false, for the site's log.</summary>
    internal const string NotInstalled =
        "Something registered after AddOptiCliMcp() replaced the MCP SDK's HttpServerTransportOptions.ConfigureSessionOptions (in a PostConfigure, or at run time): the hook that keeps the opticli MCP module's server apart from the site's other MCP servers. Without it, the module's endpoint would offer editors' assistants other servers' tools, without the module's checks and audit, so it refuses every request. Set that hook in WithHttpTransport(...) instead, or call AddOptiCliMcp() after the code that replaces it.";

    /// <summary>
    /// The module's endpoint, one of those <c>MapMcp</c> mapped under its path (which ones depends on the site's MCP
    /// transport settings): what the module serves there, and nothing else.
    /// </summary>
    /// <param name="pathSegments">The number of segments of the endpoint's own path (<see cref="OptiCliMcpOptions.McpPath"/>).</param>
    internal static void Guard(EndpointBuilder endpoint, int pathSegments)
    {
        if (endpoint.RequestDelegate is not { } next)
        {
            return;
        }
        var atPath = endpoint is RouteEndpointBuilder route && route.RoutePattern.PathSegments.Count == pathSegments;
        var method = endpoint.Metadata.OfType<HttpMethodMetadata>().LastOrDefault()?.HttpMethods is [var only] ? only : null;
        RequestDelegate served = (atPath, method) switch
        {
            // Streamable HTTP's POST, and its DELETE, which ends a session (stateful only).
            (true, "POST") or (true, "DELETE") => Sessions(next),
            // Its GET stream (stateful only) is for messages the server sends on its own: the module sends none.
            (true, "GET") => context => Refuse(context, StatusCodes.Status405MethodNotAllowed, -32000, "Method Not Allowed: this MCP endpoint takes POST only."),
            // Legacy SSE's /sse and /message (only with the site's EnableLegacySse), or whatever a later SDK maps here.
            _ => context => Refuse(context, StatusCodes.Status404NotFound, -32000, "Not Found: this MCP endpoint serves Streamable HTTP only."),
        };
        endpoint.RequestDelegate = context =>
        {
            if (!Installed(context.RequestServices.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value))
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(ToolErrors.LogCategory).LogError(NotInstalled);
                return Refuse(context, StatusCodes.Status500InternalServerError, (int)McpErrorCode.InternalError,
                    "The site turned this MCP endpoint off: its MCP setup conflicts with the opticli module. The site's log says why.");
            }
            return served(context);
        };
    }

    /// <summary>A request that names a session: only one the module's endpoint made for this connection.</summary>
    private static RequestDelegate Sessions(RequestDelegate next) => async context =>
    {
        var session = context.Request.Headers[SessionHeader].ToString();
        if (session.Length > 0)
        {
            var sessions = context.RequestServices.GetRequiredService<McpSessions>();
            if (!sessions.Owns(session, GrantOf(context.User)))
            {
                // What the SDK answers for a session it doesn't have: a client then starts a new one.
                await Refuse(context, StatusCodes.Status404NotFound, -32001, "Session not found");
                return;
            }
            if (HttpMethods.IsDelete(context.Request.Method))
            {
                await next(context);
                sessions.Ended(session);
                return;
            }
        }
        await next(context);
    };

    /// <summary>An error as the SDK writes its own: a JSON-RPC error without an id.</summary>
    private static Task Refuse(HttpContext context, int status, int code, string message)
    {
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status405MethodNotAllowed)
        {
            context.Response.Headers.Allow = HttpMethods.Post;
        }
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (object?)null, error = new { code, message } }));
    }

    /// <summary>The connection (grant) an MCP request was signed in with by the module's bearer handler; null for any other request.</summary>
    internal static string? GrantOf(ClaimsPrincipal? user) =>
        user?.Identity is { IsAuthenticated: true, AuthenticationType: TokenService.AuthenticationType } && user.FindFirst(McpClaims.Grant)?.Value is { Length: > 0 } grant
            ? grant
            : null;

    /// <summary>
    /// The module's server takes messages from <paramref name="grant"/>'s connection only. A stateless server lives for
    /// one request, so this never refuses there; a session's server lives on, and the SDK hands it any request that
    /// names its ID, at any endpoint (legacy SSE's messages even run in the request that opened the stream).
    /// </summary>
    internal static McpMessageFilter BoundTo(string? grant) => next => (context, cancellationToken) =>
        grant is not null && string.Equals(GrantOf(context.User), grant, StringComparison.Ordinal)
            ? next(context, cancellationToken)
            : throw new McpProtocolException("This MCP session belongs to another connection.", McpErrorCode.InvalidRequest);

    /// <summary>Whether this request's MCP server is one <see cref="Module"/> made: the only kind that runs the module's tools.</summary>
    internal static bool Serves(HttpContext context) =>
        context.Features.Get<McpServer>()?.ServerOptions is { } server && Made.TryGetValue(server, out _);

    /// <summary>Marks <paramref name="server"/> as the module's (<see cref="Serves"/>).</summary>
    internal static McpServerOptions Marked(McpServerOptions server)
    {
        Made.AddOrUpdate(server, Mark);
        return server;
    }

    /// <summary>
    /// The module's server: its tools (no delete tool unless the site allows deleting), filters, name and instructions,
    /// nothing else; bound to the request's connection, and its session, if the SDK made one, recorded as the module's.
    /// </summary>
    internal static void Module(HttpContext context, McpServerOptions server)
    {
        var services = context.RequestServices;
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
        var grant = GrantOf(context.User);
        server.Filters.Message.IncomingFilters.Add(BoundTo(grant));
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
        // A stateful session (the SDK set its ID on the response before it made the server): the module's, for this connection.
        if (grant is not null && context.Response.Headers[SessionHeader].ToString() is { Length: > 0 } session)
        {
            services.GetRequiredService<McpSessions>().Made(session, grant);
        }
        Marked(server);
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
