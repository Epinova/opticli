using System.Reflection;
using System.Text.Json;
using EPiServer.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>Values of the error's <c>reason</c> that only the MCP module gives, next to <see cref="AgentErrorReasons"/>.</summary>
internal static class McpErrorReasons
{
    /// <summary>The editor didn't grant this connection the scope the tool needs.</summary>
    public const string MissingScope = "missingScope";

    /// <summary>The site doesn't let assistants publish (<see cref="OptiCliMcpOptions.AllowPublish"/>).</summary>
    public const string PublishingOff = "publishingOff";

    /// <summary>The site doesn't let assistants delete (<see cref="OptiCliMcpOptions.AllowDelete"/>).</summary>
    public const string DeletingOff = "deletingOff";

    /// <summary>The CMS refused the change for the editor's access rights.</summary>
    public const string AccessDenied = "accessDenied";
}

/// <summary>
/// How a tool fails: an error result whose text is the same JSON the CLI's agent sends (<see cref="AgentError"/>:
/// <c>code</c>, <c>message</c>, <c>hint</c>, <c>reason</c>, and validation issues, the current version or a pending
/// draft where they apply), so the assistant can act on it as opticli's own skill does.
/// </summary>
/// <remarks>
/// A tool fails with a <see cref="ToolError"/>, an <see cref="McpException"/> whose message is that JSON. The SDK would
/// send it prefixed with "An error occurred invoking ..." and log every one as an error with its stack trace, so
/// <see cref="Filter"/> turns it into the error result itself: an editor's refused publish is no failure of the site.
/// Unexpected failures are logged here, and the client only learns that the site's log has the details.
/// </remarks>
internal static class ToolErrors
{
    /// <summary>The log category for tool failures the module didn't expect.</summary>
    public const string LogCategory = "OptiCli.Mcp";

    public static McpException From(AgentError error) => new ToolError(JsonSerializer.Serialize(error, AgentJson.Options));

    public static McpException Refused(string message, string hint, string reason) =>
        From(new AgentError(AgentErrorCodes.Refused, message, hint) { Reason = reason });

    public static McpException Usage(string message, string? hint = null) => From(new AgentError(AgentErrorCodes.Usage, message, hint));

    /// <summary>What the client is told about <paramref name="exception"/>; null to let it propagate as it is (cancellation).</summary>
    /// <param name="logger">Gets what the client isn't told: an unexpected failure's details.</param>
    /// <param name="customTool">The name of the site's tool that threw it, for the log; null for the module's own.</param>
    public static Exception? Map(Exception exception, ILogger logger, string? customTool = null) => exception switch
    {
        OperationCanceledException => null,
        McpException => exception,
        _ => From(ErrorOf(exception, logger, customTool)),
    };

    /// <summary>
    /// The error the client gets for <paramref name="exception"/>, neither a cancellation nor an <see cref="McpException"/>
    /// (which <see cref="Map"/> lets through): its own for a refusal of the module's or the CMS's, an internal error,
    /// logged, for anything else.
    /// </summary>
    internal static AgentError ErrorOf(Exception exception, ILogger logger, string? customTool = null)
    {
        switch (exception)
        {
            case AgentException agent:
                return agent.ToError();
            case OptiCliToolException tool:
                return new AgentError(tool.Code, tool.Message, tool.Hint);
            case ContentNotFoundException notFound:
                // A load the operation didn't guard: the same not_found as for content the editor can't read, so the
                // two stay indistinguishable. No failure of the site, so not logged as one.
                return (notFound.ContentLink is { } link && !ContentReference.IsNullOrEmpty(link)
                    ? CmsCall.NotFound(link)
                    : AgentException.NotFound(notFound.ContentGuid != Guid.Empty ? $"No content with GUID {notFound.ContentGuid}." : "No such content.")).ToError();
            case AccessDeniedException:
                // The CMS checks the editor's rights itself on save, move and delete; its message names the access level.
                return new AgentError(AgentErrorCodes.Refused,
                    $"The CMS refused this for your access rights: {exception.Message}",
                    "Tell the user they lack that access here. Leave the change as a draft, or ask someone who has the access to do it in the CMS.")
                {
                    Reason = McpErrorReasons.AccessDenied,
                };
            default:
                if (customTool is null)
                {
                    logger.LogError(exception, "An opticli MCP tool failed.");
                }
                else
                {
                    logger.LogError(exception, "The site's MCP tool {Tool} failed.", customTool);
                }
                return new AgentError(AgentErrorCodes.Internal,
                    "The site failed while running this tool; the details are in the site's log.",
                    exception.GetType().FullName);
        }
    }

    /// <summary>Sends a <see cref="ToolError"/> as the tool's error result, its JSON as the text.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (ToolError error)
            {
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Message }] };
            }
        };

    /// <summary>
    /// What a tool of the site's (<see cref="ModuleTools.IsCustom"/>) throws, as the module's error result: the same
    /// JSON the built-in tools fail with, so the instructions' "follow the hint" holds for the site's tools too. The
    /// built-in tools map their own exceptions (<see cref="ContentToolBase"/>); a site's tool can't, and without this
    /// the SDK would send its own "An error occurred invoking ..." text. For every tool, the module's own too, arguments
    /// that don't bind to the tool's parameters are a <c>usage</c> error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OptiCliToolException"/> becomes its code, message and hint. The SDK's own errors for arguments that
    /// don't bind to the tool method's parameters (a required one missing, a value that doesn't convert) become
    /// <c>usage</c>, as a built-in tool's bad argument is: the assistant can fix them. Everything else a site's tool
    /// throws is mapped as <see cref="Map"/> maps it, so a failure nobody expected is logged with the tool's name and the
    /// client only learns that the site's log has the details. A built-in tool's other exceptions pass through as before.
    /// </para>
    /// <para>
    /// An <see cref="McpException"/> passes through as it is: the SDK sends its message (with its prefix), as for any
    /// MCP server's tool. It is the SDK's documented way for a tool to send a message of its own, and the module's
    /// "only on the MCP endpoint" refusal is one.
    /// </para>
    /// </remarks>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> InnerFilter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is not McpServerTool tool)
            {
                return await next(context, cancellationToken);
            }
            var custom = context.Services?.GetService<ModuleTools>()?.IsCustom(tool) == true;
            try
            {
                return await next(context, cancellationToken);
            }
            catch (Exception e) when (e is not McpException && (custom ? Custom(e, tool, context) : Unbound(e, tool, context)) is { } error)
            {
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Message }] };
            }
        };

    /// <returns>The error a site's tool failed with; null to let the exception propagate (cancellation).</returns>
    private static Exception? Custom(Exception exception, McpServerTool tool, RequestContext<CallToolRequestParams> context)
    {
        if (Unbound(exception, tool, context) is { } usage)
        {
            return usage;
        }
        var logger = (context.Services?.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(LogCategory);
        return Map(exception, logger, tool.ProtocolTool.Name);
    }

    /// <returns>
    /// A <c>usage</c> error when <paramref name="exception"/> is the SDK's for arguments that don't bind to the tool's
    /// parameters (<see cref="ArgumentProblem"/>); null for anything else.
    /// </returns>
    private static Exception? Unbound(Exception exception, McpServerTool tool, RequestContext<CallToolRequestParams> context) =>
        exception is ArgumentException or JsonException
        && (ArgumentProblem(tool, context.Params?.Arguments)
            ?? (exception is ArgumentException { ParamName: "arguments" } && exception.TargetSite?.DeclaringType?.Assembly == typeof(AIFunction).Assembly ? exception.Message : null)) is { } problem
            ? Usage($"The arguments don't fit the tool's schema: {problem}", "Check the tool's input schema.")
            : null;

    /// <summary>
    /// Why <paramref name="arguments"/> don't bind to the parameters of the method <paramref name="tool"/> was made
    /// from, as the SDK binds them: a required argument missing, or one that doesn't convert to its parameter's type.
    /// Asked after the call failed with an <see cref="ArgumentException"/> or a <see cref="JsonException"/>, which the
    /// tool's own code may have thrown as well: the arguments tell the two apart, not the exception.
    /// </summary>
    /// <returns>Null when they bind (or the tool wasn't made from a method).</returns>
    internal static string? ArgumentProblem(McpServerTool tool, IDictionary<string, JsonElement>? arguments)
    {
        if (tool.Metadata.OfType<MethodInfo>().FirstOrDefault() is not { } method
            || tool.ProtocolTool.InputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(r => r.GetString()).ToHashSet(StringComparer.Ordinal)
            : [];
        var parameters = method.GetParameters();
        foreach (var property in properties.EnumerateObject())
        {
            if (parameters.FirstOrDefault(p => p.Name == property.Name) is not { } parameter)
            {
                continue;
            }
            if (arguments is null || !arguments.TryGetValue(property.Name, out var value))
            {
                if (required.Contains(property.Name))
                {
                    return $"the required argument '{property.Name}' is missing.";
                }
                continue;
            }
            try
            {
                JsonSerializer.Deserialize(value, parameter.ParameterType, McpJsonUtilities.DefaultOptions);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
            {
                return $"'{property.Name}': {e.Message}";
            }
        }
        return null;
    }

    /// <summary>A failure the tool reports to the assistant: the message is the error JSON.</summary>
    internal sealed class ToolError(string json) : McpException(json);
}
