namespace OptiCli.Protocol;

/// <summary>
/// Every agent response: <c>{"ok": true, "data": ..., "meta": {...}}</c> or
/// <c>{"ok": false, "error": {"code", "message", "hint", ...}, "meta": {...}}</c>.
/// </summary>
/// <typeparam name="T">The endpoint's response DTO.</typeparam>
public sealed record AgentResponse<T>(bool Ok, T? Data, AgentMeta Meta, AgentError? Error = null);

/// <param name="Source">Always <c>agent</c>, matching the CLI's <c>meta.source</c>.</param>
/// <param name="Version">Agent assembly version.</param>
/// <param name="Protocol">The <see cref="AgentProtocol.Version"/> the agent speaks.</param>
public sealed record AgentMeta(string Source, string Version, int Protocol);

/// <summary>
/// A failed request. <see cref="Code"/> is one of <see cref="AgentErrorCodes"/> and fixes the HTTP status.
/// </summary>
/// <param name="Hint">What to do next. For <c>internal</c> errors it names the exception type instead; stack traces are never sent.</param>
public sealed record AgentError(string Code, string Message, string? Hint = null)
{
    /// <summary>Every failed rule for <c>validation</c> errors, not just the first.</summary>
    public IReadOnlyList<ValidationIssue>? Validation { get; init; }

    /// <summary>For <c>conflict</c> errors: the version that is currently latest, to re-read and retry with as <c>baseVersion</c>.</summary>
    public int? CurrentVersion { get; init; }

    /// <summary>For <c>conflict</c> errors: the publish would also put these changes by someone else live; retry with <c>includeDraft</c> to confirm.</summary>
    public PendingDraft? PendingDraft { get; init; }
}

/// <summary>Error codes and the HTTP status each one is sent with.</summary>
public static class AgentErrorCodes
{
    /// <summary>400: malformed JSON, missing/unknown fields, a value that doesn't parse, a bad ref.</summary>
    public const string Usage = "usage";

    /// <summary>401: missing or wrong <see cref="AgentProtocol.TokenHeader"/>.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>403 (not Development, not loopback, protected content) or 503 (agent has no token configured).</summary>
    public const string Refused = "refused";

    /// <summary>404: content, version, content type or property does not exist, or no such route.</summary>
    public const string NotFound = "not_found";

    /// <summary>404: the route is for a protocol version this agent does not speak.</summary>
    public const string UnsupportedProtocol = "unsupported_protocol";

    /// <summary>
    /// 409: <c>baseVersion</c> is not the latest version, a publish would include someone else's unpublished changes
    /// (<see cref="AgentError.PendingDraft"/>), or the operation doesn't apply to the content's current state.
    /// </summary>
    public const string Conflict = "conflict";

    /// <summary>422: the CMS's validation rejected the content; see <see cref="AgentError.Validation"/>.</summary>
    public const string Validation = "validation";

    /// <summary>500: an unexpected failure inside the site.</summary>
    public const string Internal = "internal";

    public static int HttpStatus(string code) => code switch
    {
        Usage => 400,
        Unauthorized => 401,
        Refused => 403,
        NotFound or UnsupportedProtocol => 404,
        Conflict => 409,
        Validation => 422,
        _ => 500,
    };
}

/// <param name="Property">Property name the rule is about, null for content-level rules.</param>
/// <param name="Severity">The CMS's severity: <c>error</c> blocks the save; <c>warning</c> and <c>info</c> are reported only.</param>
public sealed record ValidationIssue(string? Property, string Message, string Severity = "error");
