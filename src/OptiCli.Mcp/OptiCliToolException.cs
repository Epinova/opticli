using OptiCli.Protocol;

namespace OptiCli.Mcp;

/// <summary>
/// How a site's own MCP tool fails on purpose: the assistant gets the same error the module's tools send, JSON with
/// <c>code</c>, <c>message</c> and <c>hint</c>, and the server's instructions tell it to follow the hint or tell the user
/// what it says. Throw it from a tool method for anything the assistant or the user can act on: an argument that names
/// nothing, a search that can't run as asked.
/// </summary>
/// <remarks>
/// Anything else a site's tool throws is a failure of the site: it is logged with the tool's name, and the assistant only
/// learns that the site's log has the details. Never put in the message what the editor may not see.
/// </remarks>
public sealed class OptiCliToolException : Exception
{
    /// <param name="code">
    /// What kind of failure, as the module's own tools name them (<see cref="AgentErrorCodes"/>): <c>usage</c> for an
    /// argument the assistant should change, <c>not_found</c> for content that doesn't exist or the editor can't read
    /// (the same answer for both), <c>refused</c> for something the editor or the site doesn't allow, <c>conflict</c> for
    /// content that changed meanwhile.
    /// </param>
    /// <param name="message">What went wrong, for the assistant and the user.</param>
    /// <param name="hint">What to do next; the assistant follows it.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> or <paramref name="message"/> is empty.</exception>
    public OptiCliToolException(string code, string message, string? hint = null)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Hint = hint;
    }

    /// <summary>What kind of failure: <c>usage</c>, <c>not_found</c>, <c>refused</c>, <c>conflict</c>, …</summary>
    public string Code { get; }

    /// <summary>What to do next, if anything.</summary>
    public string? Hint { get; }
}
