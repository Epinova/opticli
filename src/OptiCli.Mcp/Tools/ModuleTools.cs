using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Every tool the module's server offers: its own and the site's (<see cref="CustomTools"/>), made once, at startup
/// (<see cref="OptiCliMcpExtensions.MapOptiCliMcp"/>), as the MCP SDK's <c>WithTools&lt;T&gt;()</c> makes them.
/// </summary>
/// <remarks>
/// <para>
/// None of them is registered as an <see cref="McpServerTool"/> service. Those go into the SDK's one tool collection,
/// shared by every MCP server in the application, where of two tools with the same name the first registered wins: a
/// site's own <c>whoami</c> registered after the module disappeared from the site's endpoint. Only the module's server
/// gets these (<see cref="McpServerIsolation.Module"/>), so the names of the site's other servers' tools are theirs.
/// </para>
/// <para>
/// Made at startup, not per request: a clash or a tool the SDK can't make fails the start rather than the first call,
/// and the SDK works out each tool's schema once (it has been seen to list a service parameter as an argument when it
/// did that for the same method on several threads at once).
/// </para>
/// </remarks>
internal sealed class ModuleTools
{
    /// <param name="Custom">The site's, not the module's: its exceptions are mapped by <see cref="ToolErrors.InnerFilter"/>.</param>
    /// <param name="Deletes">Offered only where the site allows deleting (<see cref="OptiCliMcpOptions.AllowDelete"/>).</param>
    internal sealed record Entry(McpServerTool Tool, bool Custom, bool Deletes);

    private readonly HashSet<McpServerTool> _custom = new(ReferenceEqualityComparer.Instance);

    /// <exception cref="InvalidOperationException">A tool can't be made, or two have the same name; the message names the class and method.</exception>
    public ModuleTools(IServiceProvider services, CustomTools site)
    {
        var all = new List<Entry>();
        foreach (var type in CustomTools.BuiltInTypes)
        {
            all.AddRange(CustomTools.ToolMethods(type).Select(method => Make(method, type, services, custom: false)));
        }
        foreach (var type in site.ReadToolTypes)
        {
            foreach (var method in CustomTools.ToolMethods(type))
            {
                var entry = Make(method, type, services, custom: true);
                // What the registration checked, as the SDK made it.
                var declared = method.GetCustomAttribute<McpServerToolAttribute>()!.Name;
                if (entry.Tool.ProtocolTool.Name != declared || entry.Tool.ProtocolTool.Annotations?.ReadOnlyHint != true)
                {
                    throw new InvalidOperationException($"WithTools<{type.FullName}>(), method {method.Name}: the MCP SDK made it as '{entry.Tool.ProtocolTool.Name}', not as the read-only tool '{declared}' its [McpServerTool] declares.");
                }
                all.Add(entry);
            }
        }
        if (all.GroupBy(e => e.Tool.ProtocolTool.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new InvalidOperationException($"The opticli MCP module has two tools named '{twice.Key}': {string.Join(" and ", twice.Select(e => Describe(e.Tool)))}.");
        }
        All = all;
        _custom.UnionWith(all.Where(e => e.Custom).Select(e => e.Tool));
    }

    /// <summary>Every tool, the module's first, in the order they are offered.</summary>
    public IReadOnlyList<Entry> All { get; }

    /// <summary>The tools the module's server offers on a site with <paramref name="site"/>'s options.</summary>
    public IEnumerable<McpServerTool> Offered(OptiCliMcpOptions site) => All.Where(e => site.AllowDelete || !e.Deletes).Select(e => e.Tool);

    /// <summary>Whether <paramref name="tool"/> is one of the site's that the module made (not a copy another server made).</summary>
    public bool IsCustom(McpServerTool? tool) => tool is not null && _custom.Contains(tool);

    /// <summary>A tool of <paramref name="method"/>, as the SDK's <c>WithTools&lt;T&gt;()</c> makes it: a new instance of the class for every call, from the call's services.</summary>
    private static Entry Make(MethodInfo method, Type type, IServiceProvider services, bool custom)
    {
        McpServerTool tool;
        try
        {
            var options = new McpServerToolCreateOptions { Services = services };
            tool = method.IsStatic
                ? McpServerTool.Create(method, (object?)null, options)
                : McpServerTool.Create(method, request => CreateTarget(request.Services, type), options);
        }
        catch (Exception e) when (custom)
        {
            throw new InvalidOperationException($"WithTools<{type.FullName}>(), method {method.Name}: the MCP SDK can't make a tool of it: {e.Message}", e);
        }
        return new Entry(tool, custom, tool.ProtocolTool.Name == DeleteTool.ToolName && !custom);
    }

    /// <summary>The SDK's own: without services (never the case in a call) the class's parameterless constructor.</summary>
    private static object CreateTarget(IServiceProvider? services, Type type) =>
        services is null ? Activator.CreateInstance(type)! : ActivatorUtilities.CreateInstance(services, type);

    private static string Describe(McpServerTool tool) =>
        tool.Metadata.OfType<MethodInfo>().FirstOrDefault() is { } method ? $"{method.DeclaringType?.FullName}.{method.Name}" : tool.GetType().FullName!;
}
