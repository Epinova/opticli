using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
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

    /// <summary>A service of the request, if the site registers it.</summary>
    protected T? RequestService<T>() where T : class => http.HttpContext?.RequestServices.GetService<T>();

    /// <summary>Runs a read, or any operation whose result has no content to link to.</summary>
    /// <param name="gate">Checks beyond <paramref name="scope"/>, before anything is loaded.</param>
    protected string Run<T>(string scope, Func<CmsCall, T> operation, Action<McpEditor>? gate = null) =>
        Invoke(scope, gate, (editor, call) => JsonSerializer.Serialize(operation(call), AgentJson.Options));

    /// <summary>Runs a read of one content version: the result gets its preview URL when it isn't the published one (<see cref="ResultUrls"/>).</summary>
    protected string Read(string scope, Func<CmsCall, ContentItem> operation) =>
        Invoke(scope, null, (editor, call) => ResultUrls.Serialize(operation(call), http.HttpContext!));

    /// <summary>
    /// Runs a write: the result gets the public and edit UI URLs of the content it names, and the preview URL of the
    /// version it saved (<see cref="ResultUrls"/>).
    /// </summary>
    protected string Write(string scope, Func<CmsCall, WriteResult> operation, Action<McpEditor>? gate = null) =>
        Invoke(scope, gate, (editor, call) =>
        {
            var result = operation(call);
            return ResultUrls.Serialize(result, result.Content, http.HttpContext!, wholeContent: result.Discarded, preview: result.Saved && !result.Discarded);
        });

    /// <summary>Runs a move or a delete: the result gets the moved content's public and edit URLs.</summary>
    protected string Move(string scope, Func<CmsCall, MoveResult> operation, Action<McpEditor>? gate = null) =>
        Invoke(scope, gate, (editor, call) =>
        {
            var result = operation(call);
            return ResultUrls.Serialize(result, result.Content, http.HttpContext!, wholeContent: true);
        });

    /// <summary>Runs a read whose result is more than JSON text: an image, say.</summary>
    protected CallToolResult Show(string scope, Func<CmsCall, CallToolResult> operation) =>
        Invoke(scope, null, (editor, call) => operation(call));

    private TResult Invoke<TResult>(string scope, Action<McpEditor>? gate, Func<McpEditor, CmsCall, TResult> run)
    {
        var editor = McpEditor.From(http);
        editor.Require(scope);
        gate?.Invoke(editor);
        try
        {
            return run(editor, editor.Call(Site));
        }
        catch (Exception e) when (ToolErrors.Map(e, _logger) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>The gate for a call that publishes, unpublishes or schedules (<see cref="ToolGates.Publishing"/>), when it does.</summary>
    protected Action<McpEditor>? PublishingIf(bool publishes) => publishes ? editor => ToolGates.Publishing(editor.Scopes, Site) : null;
}
