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

    /// <summary>What kind of refusal or conflict, when a caller may act on it: one of <see cref="AgentErrorReasons"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>For <c>drift</c> errors: what differs; retry with its fingerprint in <see cref="AgentProtocol.AcceptDriftHeader"/> to confirm.</summary>
    public DriftReport? Drift { get; init; }

    /// <summary>
    /// For a removal of orphaned types or properties (<see cref="AgentRoutes.TypesRemove"/>) that stopped halfway: the full
    /// records of what was removed before it stopped.
    /// </summary>
    public OrphanRemovalResult? Removal { get; init; }
}

/// <summary>Values of <see cref="AgentError.Reason"/>, which the CLI passes on as <c>details.reason</c>.</summary>
public static class AgentErrorReasons
{
    /// <summary>A publish would put someone else's unpublished changes live (<see cref="AgentError.PendingDraft"/>).</summary>
    public const string PendingDraft = Protocol.PendingDraft.Reason;

    /// <summary>
    /// Refused: the content has an approval sequence, so it can't be published directly. Retry with
    /// <c>requestApproval</c> to start the sequence.
    /// </summary>
    public const string ApprovalSequence = "approvalSequence";

    /// <summary>Usage: <c>requestApproval</c> without <c>publish</c>, for content no approval sequence applies to.</summary>
    public const string NoApprovalSequence = "noApprovalSequence";

    /// <summary>Conflict: the content is in review; a reviewer must approve or reject it before it can be changed.</summary>
    public const string InReview = "inReview";

    /// <summary>
    /// Validation: the CMS refused to publish a language branch other than the master, because the master branch has
    /// never been published. Publish the master branch first.
    /// </summary>
    public const string MasterNotPublished = "masterNotPublished";

    /// <summary>
    /// Validation: site host changes (<see cref="AgentRoutes.SiteHosts"/>) that would break a site definition; each issue
    /// names the change, and the error's hint says what to do (not the hint for content properties).
    /// </summary>
    public const string SiteHosts = "siteHosts";

    /// <summary>
    /// Validation: ASP.NET Identity refused a new user (<see cref="AgentRoutes.UserAdd"/>): its password or name rules;
    /// each issue says which, and the error's hint applies (not the hint for content properties).
    /// </summary>
    public const string Users = "users";

    /// <summary>
    /// Validation: content can't be restored below the parent (<see cref="AgentRoutes.Restore"/>), as its type isn't
    /// allowed there; the error's hint applies.
    /// </summary>
    public const string Restore = "restore";

    /// <summary>
    /// Refused, conflict or not_found from <see cref="AgentRoutes.TypesRemove"/>: one or more named types or properties
    /// can't be removed; <see cref="AgentError.Validation"/> has each one and why, and nothing was removed.
    /// </summary>
    public const string Orphans = "orphans";
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

    /// <summary>
    /// 409: a write in shared mode while the site's code and the database differ (<see cref="AgentError.Drift"/>),
    /// without <see cref="AgentProtocol.AcceptDriftHeader"/> naming the current fingerprint.
    /// </summary>
    public const string Drift = "drift";

    /// <summary>500: an unexpected failure inside the site.</summary>
    public const string Internal = "internal";

    public static int HttpStatus(string code) => code switch
    {
        Usage => 400,
        Unauthorized => 401,
        Refused => 403,
        NotFound or UnsupportedProtocol => 404,
        Conflict or Drift => 409,
        Validation => 422,
        _ => 500,
    };
}

/// <param name="Property">Property name the rule is about, null for content-level rules.</param>
/// <param name="Severity">The CMS's severity: <c>error</c> blocks the save; <c>warning</c> and <c>info</c> are reported only.</param>
public sealed record ValidationIssue(string? Property, string Message, string Severity = "error")
{
    /// <summary>What kind of issue it is, where the CLI acts on that (<see cref="ValidationIssueCodes"/>); omitted otherwise.</summary>
    public string? Code { get; init; }
}

/// <summary>The kinds of <see cref="ValidationIssue"/> the agent names, from the CMS validator that reported them.</summary>
public static class ValidationIssueCodes
{
    /// <summary>
    /// CMS 13's check that a reference (a ContentReference, or a link in rich text or a link item) points at content that
    /// exists (<c>ReferencingContentValidator</c>); the message names the reference's ID or GUID.
    /// </summary>
    public const string UnresolvedReference = "unresolvedReference";
}
