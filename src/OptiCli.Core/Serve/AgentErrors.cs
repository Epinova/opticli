using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

/// <summary>Turns an agent error envelope into the CLI's typed errors, and so into its exit codes.</summary>
public static class AgentErrors
{
    public const string OutOfDateHint =
        "The site runs an agent from a different opticli version. Restart it with this one: `opticli serve --stop`, then `opticli serve`.";

    public static ErrorCode CodeFor(string agentCode) => agentCode switch
    {
        AgentErrorCodes.Usage => ErrorCode.Usage,
        AgentErrorCodes.NotFound or AgentErrorCodes.UnsupportedProtocol => ErrorCode.NotFound,
        AgentErrorCodes.Unauthorized or AgentErrorCodes.Refused => ErrorCode.Refused,
        AgentErrorCodes.Conflict => ErrorCode.Conflict,
        AgentErrorCodes.Validation => ErrorCode.Validation,
        _ => ErrorCode.Internal,
    };

    public static OptiCliException ToException(AgentError error)
    {
        var hint = error.Code switch
        {
            AgentErrorCodes.UnsupportedProtocol => OutOfDateHint,
            AgentErrorCodes.Unauthorized => "The token in opticli's state file doesn't match the running site. Restart it with `opticli serve --stop` and `opticli serve` (or re-run `opticli env` and restart the site).",
            AgentErrorCodes.Internal => $"The site failed unexpectedly ({error.Hint}); details are in its log (`opticli serve --logs`).",
            AgentErrorCodes.Validation => "Fix the properties listed in details.validation and retry; --dry-run checks without saving.",
            _ => error.Hint,
        };
        object? details = error.Validation is not null || error.CurrentVersion is not null
            ? new AgentErrorDetails(error.Validation, error.CurrentVersion)
            : null;
        return OptiCliException.Create(CodeFor(error.Code), error.Message, hint, details);
    }
}

/// <param name="CurrentVersion">For conflicts: the version that is latest now.</param>
public sealed record AgentErrorDetails(IReadOnlyList<ValidationIssue>? Validation, int? CurrentVersion);
