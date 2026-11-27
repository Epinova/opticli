using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// An action tool of the site's, as <see cref="OptiCliMcpBuilderExtensions.WithActionTool{T}"/> registered it: checked
/// there, made into its MCP tool at startup (<see cref="ModuleTools"/>).
/// </summary>
internal sealed record ActionToolType(Type Tool, Type Args, string Name, string? Title, string Description, ActionEffects Effects)
{
    /// <summary>How every action tool's description goes on, after what it does: the dry run, the token, a stopped run.</summary>
    internal const string DryRunWorkflow =
        "Call it with dryRun: true first and show the user the changes it reports; then, once they agree, call it again with the same arguments, dryRun: false and the planToken the dry run returned. A token runs once: if that call gets no answer, call it again with the same token, which answers with what the first call did. A run that stops at a failed target keeps the targets before it: tell the user which were done.";

    /// <summary>
    /// What an action tool's description ends with: that it changes things outside the CMS's content, what its effects do
    /// to the site, then <see cref="DryRunWorkflow"/>.
    /// </summary>
    internal static string Workflow(ActionEffects effects)
    {
        var does = new List<string>();
        if ((effects & ActionEffects.Live) != 0)
        {
            does.Add("It can change what visitors see.");
        }
        if ((effects & ActionEffects.Delete) != 0)
        {
            does.Add("It can delete things.");
        }
        if (does.Count == 0)
        {
            does.Add("It changes nothing visitors see.");
        }
        return $"It changes things outside the CMS's content, in the site's own systems, which the CMS's edit UI doesn't show. {string.Join(" ", does)} {DryRunWorkflow}";
    }

    /// <summary>The hint of a refusal where the site doesn't let assistants change what visitors see.</summary>
    internal const string PublishingOffHint =
        "Tell the user to make this change where they usually make it (the dry run's editUrl, where it has one). Nothing was changed.";

    /// <summary>Its MCP tool.</summary>
    public McpServerTool Make() => (McpServerTool)Activator.CreateInstance(typeof(ActionTool<,>).MakeGenericType(Tool, Args), this)!;
}

/// <summary>
/// The MCP tool of a site's action tool (<see cref="IOptiCliActionTool{TArgs}"/>): its arguments are
/// <typeparamref name="TArgs"/>' properties plus <c>dryRun</c> and <c>planToken</c>, as a write tool's; a call makes the
/// site's class from the request's services, gates it, and previews or runs it (<see cref="ActionRunner"/>).
/// </summary>
internal sealed class ActionTool<TTool, TArgs> : McpServerTool where TTool : class, IOptiCliActionTool<TArgs>
{
    private readonly ActionToolType _type;
    private readonly Tool _tool;
    private readonly IReadOnlyList<object> _metadata;

    public ActionTool(ActionToolType type)
    {
        _type = type;
        var schema = WriteToolType.Schema(typeof(TArgs), $"WithActionTool<{typeof(TTool).FullName}>()");
        _tool = new Tool
        {
            Name = type.Name,
            Title = type.Title,
            Description = type.Description.TrimEnd() + " " + ActionToolType.Workflow(type.Effects),
            InputSchema = JsonSerializer.SerializeToElement(schema),
            Annotations = new ToolAnnotations
            {
                Title = type.Title,
                ReadOnlyHint = false,
                DestructiveHint = (type.Effects & (ActionEffects.Live | ActionEffects.Delete)) != 0,
                IdempotentHint = false,
                // It reaches past the CMS's content: the site's own systems, maybe another service.
                OpenWorldHint = true,
            },
        };
        _metadata = [.. typeof(TTool).GetCustomAttributes(inherit: false)];
    }

    public override Tool ProtocolTool => _tool;

    /// <summary>The site's class's attributes. No <see cref="MethodInfo"/>: no other server can make a tool of the class.</summary>
    public override IReadOnlyList<object> Metadata => _metadata;

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services ?? throw new InvalidOperationException("The MCP SDK called the tool without services.");
        var editor = McpEditor.From(services.GetRequiredService<IHttpContextAccessor>());
        editor.Require(Scopes.Write);
        var site = services.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value;
        if ((_type.Effects & ActionEffects.Delete) != 0)
        {
            // Not offered without it (ModuleTools); should it be called regardless.
            ToolGates.Deleting(site);
        }
        if (_type.Effects == ActionEffects.Live)
        {
            // Every call would change what visitors see: refused before the site's system is asked for a preview.
            ToolGates.Publishing(editor.Scopes, site, ActionToolType.PublishingOffHint);
        }
        var (args, normalized, dryRun, planToken) = WriteToolType.Arguments<TArgs>(request.Params?.Arguments);
        if (!dryRun && planToken is null)
        {
            throw ToolErrors.Usage("This tool runs only with the planToken of its dry run. Nothing was changed.",
                "Call it with dryRun: true first and show the user the changes; then call it again with the same arguments, dryRun: false and the planToken the dry run returned.");
        }

        var runner = new ActionRunner(
            _type, site, editor.Name, editor.ClientId, editor.Scopes, services.GetRequiredService<IActionRunStore>(),
            services.GetService<McpAudit>(), services.GetRequiredService<ILoggerFactory>().CreateLogger(ToolErrors.LogCategory),
            services.GetRequiredService<IDataProtectionProvider>(), services.GetRequiredService<TimeProvider>());
        var tool = ActivatorUtilities.CreateInstance<TTool>(services);
        try
        {
            async Task<ActionPreview> Preview() => await tool.PreviewAsync(args, cancellationToken)
                ?? throw new InvalidOperationException($"The site's action tool {_type.Name} returned no preview: return an empty ActionPreview when there is nothing to do.");
            return dryRun
                ? runner.DryRun(await Preview(), normalized)
                : await runner.RunAsync(normalized, planToken!, Preview, (preview, run) => tool.RunAsync(args, preview, run, cancellationToken), cancellationToken);
        }
        finally
        {
            if (tool is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (tool is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

/// <summary>The checks <see cref="OptiCliMcpBuilderExtensions.WithActionTool{T}"/> makes of an action tool class.</summary>
internal static class ActionToolTypes
{
    /// <summary>The action tool <paramref name="type"/> declares, checked: everything the module needs to make its MCP tool.</summary>
    /// <exception cref="InvalidOperationException">What is wrong with it, naming the class.</exception>
    public static ActionToolType Of(Type type, Action<string, string> checkName)
    {
        var where = $"WithActionTool<{type.FullName}>()";
        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            throw new InvalidOperationException($"{where}: the module makes the class for every call, so register a class it can make (not abstract, not an open generic).");
        }
        var interfaces = type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOptiCliActionTool<>)).ToList();
        if (interfaces.Count != 1)
        {
            throw new InvalidOperationException(interfaces.Count == 0
                ? $"{where}: the class doesn't implement IOptiCliActionTool<TArgs>."
                : $"{where}: the class implements IOptiCliActionTool<TArgs> for more than one arguments type ({string.Join(", ", interfaces.Select(i => i.GetGenericArguments()[0].Name))}); an action tool has one.");
        }
        if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOptiCliWriteTool<>)))
        {
            throw new InvalidOperationException($"{where}: the class is a write tool too (IOptiCliWriteTool<TArgs>). A class is one tool: make the action and the write tool classes of their own.");
        }
        var attribute = type.GetCustomAttribute<OptiCliActionToolAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"{where}: the class has no [OptiCliActionTool]. Give it its name and effects, e.g. [OptiCliActionTool(\"publish_event\", Title = \"Publish event\", Effects = ActionEffects.Live)].");
        if (string.IsNullOrEmpty(attribute.Name))
        {
            throw new InvalidOperationException($"{where}: [OptiCliActionTool] has no name.");
        }
        checkName(attribute.Name, where);
        var all = Enum.GetValues<ActionEffects>().Aggregate(ActionEffects.None, (a, e) => a | e);
        if (attribute.Effects == ActionEffects.None || (attribute.Effects & ~all) != 0)
        {
            throw new InvalidOperationException($"{where}: tool '{attribute.Name}' declares no effects, or unknown ones ({attribute.Effects}). Set Effects in [OptiCliActionTool], e.g. Effects = ActionEffects.Change | ActionEffects.Live.");
        }
        var description = type.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException($"{where}: tool '{attribute.Name}' has no [Description] on its class. Say what it changes: the assistant decides from it when to use the tool.");
        }
        if (CustomTools.ToolMethods(type).FirstOrDefault() is { } method)
        {
            throw new InvalidOperationException($"{where}: method {method.Name} has [McpServerTool]. An action tool class has no tool methods: put read tools in a class of their own (WithTools).");
        }
        var args = interfaces[0].GetGenericArguments()[0];
        WriteToolType.Schema(args, where);
        return new ActionToolType(type, args, attribute.Name, string.IsNullOrWhiteSpace(attribute.Title) ? null : attribute.Title, description.Trim(), attribute.Effects);
    }
}
