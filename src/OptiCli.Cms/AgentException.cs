using OptiCli.Protocol;

namespace OptiCli.Cms;

/// <summary>
/// An expected failure, sent to the caller as an error envelope. Anything else thrown while handling
/// a request becomes an <c>internal</c> error.
/// </summary>
internal sealed class AgentException(string code, string message, string? hint = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? Hint { get; } = hint;

    public IReadOnlyList<ValidationIssue>? Validation { get; init; }

    public int? CurrentVersion { get; init; }

    public PendingDraft? PendingDraft { get; init; }

    /// <summary>One of <see cref="AgentErrorReasons"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>For <c>drift</c>: what differs.</summary>
    public DriftReport? Drift { get; init; }

    /// <summary>Overrides the code's usual status, e.g. 503 rather than 403 for a disabled agent.</summary>
    public int? ForcedStatus { get; init; }

    public int Status => ForcedStatus ?? AgentErrorCodes.HttpStatus(Code);

    public AgentError ToError() => new(Code, Message, Hint) { Validation = Validation, CurrentVersion = CurrentVersion, PendingDraft = PendingDraft, Reason = Reason ?? (PendingDraft is null ? null : AgentErrorReasons.PendingDraft), Drift = Drift };

    public static AgentException Usage(string message, string? hint = null) => new(AgentErrorCodes.Usage, message, hint);

    public static AgentException NotFound(string message, string? hint = null) => new(AgentErrorCodes.NotFound, message, hint);

    public static AgentException Refused(string message, string? hint = null) => new(AgentErrorCodes.Refused, message, hint);

    public static AgentException Conflict(string message, string? hint = null) => new(AgentErrorCodes.Conflict, message, hint);

    /// <param name="issues">Every issue found; warnings are passed along but don't count as errors.</param>
    /// <param name="hint">What to do instead of fixing the listed errors, for a known <paramref name="reason"/>.</param>
    public static AgentException Invalid(IReadOnlyList<ValidationIssue> issues, string? hint = null, string? reason = null)
    {
        var errors = issues.Where(i => i.Severity == "error").ToList();
        return new(
            AgentErrorCodes.Validation,
            errors.Count == 1 ? $"Validation failed: {Describe(errors[0])}" : $"Validation failed with {errors.Count} errors.",
            hint ?? "Fix every listed error and retry; use dryRun to check without saving.")
        {
            Validation = issues,
            Reason = reason,
        };
    }

    private static string Describe(ValidationIssue issue) => issue.Property is null ? issue.Message : $"{issue.Property}: {issue.Message}";
}
