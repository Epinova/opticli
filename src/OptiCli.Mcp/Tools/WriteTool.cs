using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// A write tool of the site's, as <see cref="OptiCliMcpBuilderExtensions.WithWriteTool{T}"/> registered it: checked
/// there, made into its MCP tool at startup (<see cref="ModuleTools"/>).
/// </summary>
internal sealed record WriteToolType(Type Tool, Type Args, string Name, string? Title, string Description, ContentPlanKinds Kinds)
{
    /// <summary>The argument that asks for the dry run.</summary>
    public const string DryRun = "dryRun";

    /// <summary>The argument that carries the dry run's plan token to the real run.</summary>
    public const string PlanToken = "planToken";

    internal const string DryRunDescription = "Only check and report the changes, saving nothing: do this first and show the user the changes.";

    internal const string PlanTokenDescription = "The planToken the dry run returned; required when dryRun is false.";

    /// <summary>What every write tool's description ends with.</summary>
    internal const string Workflow =
        "It saves drafts only, never publishing. Call it with dryRun: true first and show the user the steps it reports; then, once they agree, call it again with the same arguments, dryRun: false and the planToken the dry run returned. A run that stops at a failed step keeps the steps before it: tell the user which were saved.";

    /// <summary>How the arguments are read: a required one missing, <c>null</c> for one that isn't nullable, or one the type doesn't have, is refused.</summary>
    internal static readonly JsonSerializerOptions Strict = new(McpJsonUtilities.DefaultOptions)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Its MCP tool.</summary>
    public McpServerTool Make() => (McpServerTool)Activator.CreateInstance(typeof(WriteTool<,>).MakeGenericType(Tool, Args), this)!;

    /// <summary>
    /// The tool's input schema: <paramref name="args"/>' properties, as the MCP SDK describes a type, plus
    /// <see cref="DryRun"/> and <see cref="PlanToken"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type isn't an object with properties of its own names, or can't be read from JSON; the message says why.</exception>
    public static JsonObject Schema(Type args, string where)
    {
        JsonObject schema;
        try
        {
            schema = JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(args, serializerOptions: Strict).GetRawText()) as JsonObject
                ?? throw new InvalidOperationException("no schema");
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException($"{where}: the MCP SDK can't describe the arguments type {args.FullName}: {e.Message}", e);
        }
        var type = schema["type"];
        var isObject = type is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == "object"
            || type is JsonArray types && types.Any(t => t?.GetValue<string>() == "object");
        if (!isObject)
        {
            throw new InvalidOperationException($"{where}: the arguments type {args.FullName} isn't an object with properties. Use a record or class whose properties are the tool's arguments.");
        }
        schema["type"] = "object";
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        schema["properties"] = properties;
        if (properties.FirstOrDefault(p => p.Key.Equals(DryRun, StringComparison.OrdinalIgnoreCase) || p.Key.Equals(PlanToken, StringComparison.OrdinalIgnoreCase)) is { Key: { } taken })
        {
            throw new InvalidOperationException($"{where}: the arguments type {args.FullName} has a property '{taken}': dryRun and planToken are the module's own arguments of every write tool. Rename it.");
        }
        try
        {
            // A required argument missing is the assistant's to fix; a type JSON can't make at all is the site's.
            JsonSerializer.Deserialize("{}", args, Strict);
        }
        catch (JsonException)
        {
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{where}: the arguments type {args.FullName} can't be read from JSON: {e.Message}", e);
        }
        properties[DryRun] = new JsonObject { ["type"] = "boolean", ["description"] = DryRunDescription, ["default"] = false };
        properties[PlanToken] = new JsonObject { ["type"] = "string", ["description"] = PlanTokenDescription };
        schema["additionalProperties"] = false;
        return schema;
    }
}

/// <summary>
/// The MCP tool of a site's write tool (<see cref="IOptiCliWriteTool{TArgs}"/>): its arguments are
/// <typeparamref name="TArgs"/>' properties plus <c>dryRun</c> and <c>planToken</c>; a call makes the site's class from
/// the request's services, asks it for its plan, and checks or runs the plan as the editor (<see cref="PlanRunner"/>).
/// </summary>
/// <remarks>
/// Made by hand rather than with <c>McpServerTool.Create</c>, which would nest the arguments under one parameter: a write
/// tool takes its arguments flat, as the module's own tools do, and reports a bad one as <c>usage</c>.
/// </remarks>
internal sealed class WriteTool<TTool, TArgs> : McpServerTool where TTool : class, IOptiCliWriteTool<TArgs>
{
    private readonly WriteToolType _type;
    private readonly Tool _tool;
    private readonly IReadOnlyList<object> _metadata;

    public WriteTool(WriteToolType type)
    {
        _type = type;
        var schema = WriteToolType.Schema(typeof(TArgs), $"WithWriteTool<{typeof(TTool).FullName}>()");
        _tool = new Tool
        {
            Name = type.Name,
            Title = type.Title,
            Description = type.Description.TrimEnd() + " " + WriteToolType.Workflow,
            InputSchema = JsonSerializer.SerializeToElement(schema),
            Annotations = new ToolAnnotations
            {
                Title = type.Title,
                ReadOnlyHint = false,
                DestructiveHint = (type.Kinds & (ContentPlanKinds.Move | ContentPlanKinds.Trash)) != 0,
                IdempotentHint = false,
                OpenWorldHint = false,
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
        if ((_type.Kinds & ContentPlanKinds.Trash) != 0)
        {
            // Not offered without it (ModuleTools); should it be called regardless.
            ToolGates.Deleting(site);
        }
        var (args, normalized, dryRun, planToken) = Arguments(request.Params?.Arguments);
        if (!dryRun && planToken is null)
        {
            throw ToolErrors.Usage("This tool runs only with the planToken of its dry run. Nothing was changed.",
                "Call it with dryRun: true first and show the user the changes; then call it again with the same arguments, dryRun: false and the planToken the dry run returned.");
        }

        var plan = await Plan(services, args, cancellationToken);
        var runner = new PlanRunner(
            _type.Name, _type.Kinds, site.MaxPlanSteps, services.GetRequiredService<IContentPlanSteps>(), editor.Name, editor.ClientId,
            services.GetService<McpAudit>(), services.GetRequiredService<ILoggerFactory>().CreateLogger(ToolErrors.LogCategory),
            services.GetRequiredService<IDataProtectionProvider>());
        return dryRun ? runner.DryRun(plan, normalized) : runner.Run(plan, normalized, planToken!, cancellationToken);
    }

    /// <summary>The site's plan, from an instance made for this call and disposed after it, as the SDK does for a tool class.</summary>
    private async Task<ContentPlan> Plan(IServiceProvider services, TArgs args, CancellationToken cancellationToken)
    {
        var tool = ActivatorUtilities.CreateInstance<TTool>(services);
        try
        {
            return await tool.PlanAsync(args, cancellationToken)
                ?? throw new InvalidOperationException($"The site's write tool {_type.Name} returned no plan: return an empty ContentPlan when there is nothing to change.");
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

    /// <summary>The tool's own arguments, read strictly, and the module's.</summary>
    /// <returns>The arguments, and them again as JSON as the tool read them (with its defaults), for the plan token.</returns>
    /// <exception cref="McpException"><c>usage</c> for arguments that don't fit.</exception>
    private static (TArgs Args, JsonNode Normalized, bool DryRun, string? PlanToken) Arguments(IDictionary<string, JsonElement>? arguments)
    {
        var own = new JsonObject();
        var dryRun = false;
        string? planToken = null;
        foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
        {
            switch (name)
            {
                case WriteToolType.DryRun:
                    dryRun = value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False or JsonValueKind.Null => false,
                        _ => throw ToolErrors.Usage("The arguments don't fit the tool's schema: 'dryRun' must be true or false.", "Check the tool's input schema."),
                    };
                    break;
                case WriteToolType.PlanToken:
                    planToken = value.ValueKind switch
                    {
                        JsonValueKind.String when value.GetString() is { Length: > 0 } token => token,
                        JsonValueKind.String or JsonValueKind.Null => null,
                        _ => throw ToolErrors.Usage("The arguments don't fit the tool's schema: 'planToken' must be the string the dry run returned.", "Check the tool's input schema."),
                    };
                    break;
                default:
                    own[name] = JsonNode.Parse(value.GetRawText());
                    break;
            }
        }
        try
        {
            var args = own.Deserialize<TArgs>(WriteToolType.Strict)
                ?? throw ToolErrors.Usage("The arguments don't fit the tool's schema: they are missing.", "Check the tool's input schema.");
            return (args, JsonSerializer.SerializeToNode(args, WriteToolType.Strict) ?? new JsonObject(), dryRun, planToken);
        }
        catch (JsonException e)
        {
            throw ToolErrors.Usage($"The arguments don't fit the tool's schema: {e.Message}", "Check the tool's input schema.");
        }
    }
}

/// <summary>The checks <see cref="OptiCliMcpBuilderExtensions.WithWriteTool{T}"/> makes of a write tool class.</summary>
internal static class WriteToolTypes
{
    /// <summary>The write tool <paramref name="type"/> declares, checked: everything the module needs to make its MCP tool.</summary>
    /// <exception cref="InvalidOperationException">What is wrong with it, naming the class.</exception>
    public static WriteToolType Of(Type type, Action<string, string> checkName)
    {
        var where = $"WithWriteTool<{type.FullName}>()";
        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            throw new InvalidOperationException($"{where}: the module makes the class for every call, so register a class it can make (not abstract, not an open generic).");
        }
        var interfaces = type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOptiCliWriteTool<>)).ToList();
        if (interfaces.Count != 1)
        {
            throw new InvalidOperationException(interfaces.Count == 0
                ? $"{where}: the class doesn't implement IOptiCliWriteTool<TArgs>."
                : $"{where}: the class implements IOptiCliWriteTool<TArgs> for more than one arguments type ({string.Join(", ", interfaces.Select(i => i.GetGenericArguments()[0].Name))}); a write tool has one.");
        }
        var attribute = type.GetCustomAttribute<OptiCliWriteToolAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"{where}: the class has no [OptiCliWriteTool]. Give it its name and kinds, e.g. [OptiCliWriteTool(\"retag_articles\", Title = \"Retag articles\", Kinds = ContentPlanKinds.Update)].");
        if (string.IsNullOrEmpty(attribute.Name))
        {
            throw new InvalidOperationException($"{where}: [OptiCliWriteTool] has no name.");
        }
        checkName(attribute.Name, where);
        var all = Enum.GetValues<ContentPlanKinds>().Aggregate(ContentPlanKinds.None, (a, k) => a | k);
        if (attribute.Kinds == ContentPlanKinds.None || (attribute.Kinds & ~all) != 0)
        {
            throw new InvalidOperationException($"{where}: tool '{attribute.Name}' declares no kinds of steps, or unknown ones ({attribute.Kinds}). Set Kinds in [OptiCliWriteTool], e.g. Kinds = ContentPlanKinds.Update | ContentPlanKinds.Create.");
        }
        var description = type.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException($"{where}: tool '{attribute.Name}' has no [Description] on its class. Say what it changes: the assistant decides from it when to use the tool.");
        }
        if (CustomTools.ToolMethods(type).FirstOrDefault() is { } method)
        {
            throw new InvalidOperationException($"{where}: method {method.Name} has [McpServerTool]. A write tool class has no tool methods: put read tools in a class of their own (WithTools).");
        }
        var args = interfaces[0].GetGenericArguments()[0];
        WriteToolType.Schema(args, where);
        return new WriteToolType(type, args, attribute.Name, string.IsNullOrWhiteSpace(attribute.Title) ? null : attribute.Title, description.Trim(), attribute.Kinds);
    }
}
