using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// What every content tool does around its operation: who the editor is, the scope and site gates, the operation as
/// the editor (<see cref="CmsCaller.Editor"/>), the result as JSON in the agent's shape, and errors as
/// <see cref="ToolErrors"/> gives them.
/// </summary>
internal abstract class ContentToolBase(IHttpContextAccessor http, IOptions<OptiCliMcpOptions> options, ILoggerFactory loggers)
{
    private readonly ILogger _logger = loggers.CreateLogger(ToolErrors.LogCategory);

    protected OptiCliMcpOptions Site => options.Value;

    /// <summary>Runs a read, or any operation whose result has no content to link to.</summary>
    /// <param name="gate">Checks beyond <paramref name="scope"/>, before anything is loaded.</param>
    protected string Run<T>(string scope, Func<CmsCall, T> operation, Action<McpEditor>? gate = null) =>
        Invoke(scope, gate, (editor, call) => JsonSerializer.Serialize(operation(call), AgentJson.Options));

    /// <summary>Runs a write: the result gets the edit UI's URL of the content it names (<see cref="EditUrls"/>).</summary>
    protected string Write(string scope, Func<CmsCall, WriteResult> operation, Action<McpEditor>? gate = null) =>
        Invoke(scope, gate, (editor, call) =>
        {
            var result = operation(call);
            return EditUrls.Serialize(result, result.Content, http.HttpContext!, wholeContent: result.Discarded);
        });

    /// <summary>Runs a move or a delete: the result gets the moved content's edit URL.</summary>
    protected string Move(string scope, Func<CmsCall, MoveResult> operation, Action<McpEditor>? gate = null) =>
        Invoke(scope, gate, (editor, call) =>
        {
            var result = operation(call);
            return EditUrls.Serialize(result, result.Content, http.HttpContext!, wholeContent: true);
        });

    private string Invoke(string scope, Action<McpEditor>? gate, Func<McpEditor, CmsCall, string> run)
    {
        var editor = McpEditor.From(http);
        editor.Require(scope);
        gate?.Invoke(editor);
        try
        {
            return run(editor, editor.Call());
        }
        catch (Exception e) when (ToolErrors.Map(e, _logger) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>The gate for a call that publishes, unpublishes or schedules (<see cref="ToolGates.Publishing"/>), when it does.</summary>
    protected Action<McpEditor>? PublishingIf(bool publishes) => publishes ? editor => ToolGates.Publishing(editor.Scopes, Site) : null;
}
