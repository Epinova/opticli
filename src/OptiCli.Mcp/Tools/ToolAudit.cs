using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Audits every tool call in one place, around the tool: the editor, the client, the tool, the content references its
/// arguments name, and the outcome. Only arguments that name content are logged, never property values or other text.
/// </summary>
internal static class ToolAudit
{
    /// <summary>Argument names that hold a content reference; tools name theirs this way so they're audited.</summary>
    internal static readonly HashSet<string> ReferenceArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "reference", "ref", "parent", "destination", "root", "contentId", "target", "forContent", "replace",
    };

    private const int MaxReferences = 10;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var audit = context.Services?.GetService(typeof(McpAudit)) as McpAudit;
            var user = context.User;
            var name = user?.Identity?.Name ?? "";
            var client = user?.FindFirst(McpClaims.Client)?.Value ?? "";
            var tool = context.Params?.Name ?? "";
            var references = References(context.Params?.Arguments);
            try
            {
                var result = await next(context, cancellationToken);
                audit?.ToolCall(name, client, tool, references, result.IsError == true ? "failed" : "ok");
                return result;
            }
            catch (Exception e)
            {
                audit?.ToolCall(name, client, tool, references, e is OperationCanceledException ? "cancelled" : "failed");
                throw;
            }
        };

    internal static IReadOnlyList<string> References(IDictionary<string, JsonElement>? arguments) =>
        arguments is null
            ? []
            : arguments
                .Where(a => ReferenceArguments.Contains(a.Key) && a.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                .Select(a => a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString()! : a.Value.GetRawText())
                .Select(r => r.Length > 100 ? r[..100] : r)
                .Take(MaxReferences)
                .ToList();
}
