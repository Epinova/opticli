using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// What the site added to the module's server through <see cref="IOptiCliMcpBuilder"/>: its tool types, their names, and
/// its lines for the instructions. One per site, a singleton; filled while the site registers its services, read once
/// the site runs.
/// </summary>
/// <remarks>
/// The names are checked here, as the site registers each type, so a clash fails at the line that causes it. The tools
/// themselves are made later, at startup (<see cref="ModuleTools"/>): making one needs the site's services.
/// </remarks>
internal sealed partial class CustomTools
{
    /// <summary>The module's own tool classes, in the order the module offers their tools.</summary>
    internal static readonly Type[] BuiltInTypes = [typeof(WhoAmITool), typeof(ReadTools), typeof(WriteTools), typeof(PublishTools), typeof(DeleteTool)];

    /// <summary>The built-in tools' names, every one of them, also those a site may not be offered (<c>delete_content</c>).</summary>
    internal static readonly IReadOnlySet<string> BuiltInNames = BuiltInTypes.SelectMany(ToolMethods).Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!).ToHashSet(StringComparer.Ordinal);

    private readonly List<Type> _readTools = [];
    private readonly HashSet<Type> _types = [];
    private readonly HashSet<Type> _declaring = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly List<string> _instructions = [];

    /// <summary>The read tool classes, in the order the site registered them.</summary>
    public IReadOnlyList<Type> ReadToolTypes => _readTools;

    /// <summary>The site's lines for the server instructions, in order.</summary>
    public IReadOnlyList<string> Instructions => _instructions;

    /// <summary>The registry in <paramref name="services"/>, added the first time.</summary>
    public static CustomTools Of(IServiceCollection services)
    {
        if (services.LastOrDefault(s => s.ServiceType == typeof(CustomTools))?.ImplementationInstance is CustomTools found)
        {
            return found;
        }
        var registry = new CustomTools();
        services.AddSingleton(registry);
        return registry;
    }

    /// <summary>The methods the SDK's <c>WithTools&lt;T&gt;()</c> makes tools of: every one with <c>[McpServerTool]</c>, public or not, static or not.</summary>
    internal static IEnumerable<MethodInfo> ToolMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    /// <summary>
    /// Records a class of read tools, after checking every <c>[McpServerTool]</c> method of it: a name of its own that
    /// is a valid MCP tool name, and <c>ReadOnly = true</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The class isn't a valid set of read tools; the message says why, naming the class and method.</exception>
    public void AddReadTools(Type type)
    {
        RefuseTwice(type);
        var methods = ToolMethods(type).ToList();
        if (methods.Count == 0)
        {
            throw new InvalidOperationException(
                $"WithTools<{type.FullName}>(): the class has no method with [McpServerTool]. Mark each of its tool methods with [McpServerTool(Name = \"...\", ReadOnly = true)].");
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
            var where = $"WithTools<{type.FullName}>(), method {method.Name}";
            if (string.IsNullOrEmpty(tool.Name))
            {
                // The SDK would make one up from the method's name; the module needs it now, to check it against the others.
                throw new InvalidOperationException($"{where}: [McpServerTool] has no Name. Give every tool of the site its name, e.g. [McpServerTool(Name = \"search_products\", ReadOnly = true)].");
            }
            if (!tool.ReadOnly)
            {
                throw new InvalidOperationException(
                    $"{where}: tool '{tool.Name}' isn't ReadOnly = true. WithTools takes tools that only read: set ReadOnly = true in its [McpServerTool], or make a tool that changes content a write tool (WithWriteTool), which runs its changes through the module's dry run and drafts.");
            }
            if (!method.IsStatic && (type.IsAbstract || type.IsInterface))
            {
                throw new InvalidOperationException($"{where}: tool '{tool.Name}' is an instance method of an abstract class; the module makes the class for every call, so register a class it can make.");
            }
            CheckName(tool.Name, where);
            if (!names.TryAdd(tool.Name, method.Name))
            {
                throw new InvalidOperationException($"{where}: the class has two tools named '{tool.Name}' (methods {names[tool.Name]} and {method.Name}).");
            }
        }
        foreach (var (name, method) in names)
        {
            _names.Add(name, $"{type.FullName}.{method}");
        }
        _types.Add(type);
        // A tool method a base class declares is made with the base class's MethodInfo, also by another server.
        _declaring.UnionWith(methods.Select(m => m.DeclaringType!));
        _readTools.Add(type);
    }

    /// <summary>
    /// Checks <paramref name="name"/> for a tool of the site's: a valid MCP tool name, no built-in tool's (whether or not
    /// the site offers it), no other custom tool's.
    /// </summary>
    /// <param name="where">The registration and member it comes from, for the message.</param>
    /// <exception cref="InvalidOperationException">The name can't be used; the message names <paramref name="where"/>.</exception>
    public void CheckName(string name, string where)
    {
        if (!ToolName().IsMatch(name))
        {
            throw new InvalidOperationException($"{where}: '{name}' isn't a valid MCP tool name: 1 to 128 of the letters A-Z and a-z, digits, '_', '-' and '.'.");
        }
        if (BuiltInNames.Contains(name))
        {
            throw new InvalidOperationException($"{where}: '{name}' is the name of one of the opticli module's own tools. Give the site's tool a name of its own.");
        }
        if (_names.TryGetValue(name, out var other))
        {
            throw new InvalidOperationException($"{where}: '{name}' is already the name of the site's tool {other}. Give each tool a name of its own.");
        }
    }

    /// <exception cref="InvalidOperationException"><paramref name="type"/> was registered before.</exception>
    private void RefuseTwice(Type type)
    {
        if (_types.Contains(type))
        {
            throw new InvalidOperationException($"{type.FullName} is registered twice with the opticli MCP module. Register each tool class once.");
        }
    }

    /// <summary>Adds a line to the instructions.</summary>
    public void AddInstructions(string text) => _instructions.Add(text);

    /// <summary>
    /// Whether <paramref name="tool"/> was made from a method of one of the site's tool classes: by the module, or by
    /// another MCP server the site registered the same class with. The SDK's metadata names the method.
    /// </summary>
    public bool Has(McpServerTool tool) => tool.Metadata.OfType<MethodInfo>().Any(m => m.DeclaringType is { } type && _declaring.Contains(type));

    /// <summary>The name check the SDK applies to the tools it makes (and the module to those it makes itself).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,128}\z")]
    private static partial Regex ToolName();
}
