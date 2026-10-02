using System.Text.Json;
using EPiServer.Core;
using Microsoft.Extensions.Logging;
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
    public static Exception? Map(Exception exception, ILogger logger)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return null;
            case McpException:
                return exception;
            case AgentException agent:
                return From(agent.ToError());
            case AccessDeniedException:
                // The CMS checks the editor's rights itself on save, move and delete; its message names the access level.
                return Refused(
                    $"The CMS refused this for your access rights: {exception.Message}",
                    "Tell the user they lack that access here. Leave the change as a draft, or ask someone who has the access to do it in the CMS.",
                    McpErrorReasons.AccessDenied);
            default:
                logger.LogError(exception, "An opticli MCP tool failed.");
                return From(new AgentError(AgentErrorCodes.Internal,
                    "The site failed while running this tool; the details are in the site's log.",
                    exception.GetType().FullName));
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

    /// <summary>A failure the tool reports to the assistant: the message is the error JSON.</summary>
    internal sealed class ToolError(string json) : McpException(json);
}
