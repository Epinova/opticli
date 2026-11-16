using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// Audits every tool call in one place, around the tool: the editor, the client, the tool, the content references its
/// arguments name, the CMS project it changed or saved into, and the outcome. Only arguments that name content or a
/// project are logged, never property values or other text.
/// </summary>
internal static class ToolAudit
{
    /// <summary>Argument names that hold a content reference; tools name theirs this way so they're audited.</summary>
    internal static readonly HashSet<string> ReferenceArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "reference", "ref", "parent", "destination", "root", "contentId", "target", "forContent", "replace",
    };

    /// <summary>Argument names that hold a list of content references (the project tools' items).</summary>
    internal static readonly HashSet<string> ReferenceListArguments = new(StringComparer.OrdinalIgnoreCase) { "items" };

    /// <summary>The argument that names a CMS project (an id or a name), on the project tools and the save tools.</summary>
    internal const string ProjectArgument = "project";

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
            var project = Project(context.Params?.Arguments);
            try
            {
                var result = await next(context, cancellationToken);
                audit?.ToolCall(name, client, tool, references, result.IsError == true ? "failed" : "ok", Project(project, result));
                return result;
            }
            catch (Exception e)
            {
                audit?.ToolCall(name, client, tool, references, e is OperationCanceledException ? "cancelled" : "failed", project);
                throw;
            }
        };

    internal static IReadOnlyList<string> References(IDictionary<string, JsonElement>? arguments) =>
        arguments is null
            ? []
            : arguments
                .SelectMany(a => ReferenceArguments.Contains(a.Key) ? [a.Value]
                    : ReferenceListArguments.Contains(a.Key) && a.Value.ValueKind == JsonValueKind.Array ? a.Value.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>())
                .Where(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                .Select(Text)
                .Take(MaxReferences)
                .ToList();

    /// <summary>The project the arguments name, as given; null when they name none.</summary>
    internal static string? Project(IDictionary<string, JsonElement>? arguments) =>
        arguments is not null && arguments.TryGetValue(ProjectArgument, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            && Text(value) is { Length: > 0 } project
            ? project
            : null;

    /// <summary>
    /// The project of a call as the audit names it: its id, from a result that names one (<c>project.id</c>: the project
    /// tools', and a save's into a project), with the project as the arguments named it when that was otherwise (a name);
    /// else as the arguments named it.
    /// </summary>
    internal static string? Project(string? named, CallToolResult result)
    {
        if (result.IsError == true || result.Content is not [TextContentBlock { Text: var text }] || !text.Contains("\"project\"", StringComparison.Ordinal))
        {
            return named;
        }
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(ProjectArgument, out var project)
                && project.ValueKind == JsonValueKind.Object && project.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var id) && id > 0)
            {
                var shown = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return named is null || named == shown ? shown : $"{shown} ({named})";
            }
        }
        catch (JsonException)
        {
        }
        return named;
    }

    private static string Text(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return text.Length > 100 ? text[..100] : text;
    }
}
