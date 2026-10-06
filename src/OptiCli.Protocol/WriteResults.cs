using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptiCli.Protocol;

/// <summary>Identity and state of one content version.</summary>
public sealed record ContentSummary
{
    /// <summary>Version-specific ref (<c>id_version</c>) that <c>get</c> accepts.</summary>
    public required string Ref { get; init; }

    public required int Id { get; init; }

    /// <summary>Version id (<c>tblWorkContent.pkID</c>).</summary>
    public int? Version { get; init; }

    public required Guid Guid { get; init; }

    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Language { get; init; }

    /// <summary>CMS version status, camelCase: <c>checkedOut</c> (draft), <c>published</c>, <c>previouslyPublished</c>, ...</summary>
    public string? Status { get; init; }

    public string? Parent { get; init; }
}

/// <summary>Response of create, draft, languages and publish.</summary>
public sealed record WriteResult
{
    /// <summary>The saved version; for a dry run the version the change was applied to (null for a dry-run create).</summary>
    public ContentSummary? Content { get; init; }

    /// <summary>True when a version was written.</summary>
    public bool Saved { get; init; }

    public bool Published { get; init; }

    /// <summary>
    /// Unpublish: the published version was copied with its stop-publish date set to now and that copy published, so the
    /// content is offline (expired). <see cref="PreviouslyPublished"/> is the version that was live.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unpublished { get; init; }

    /// <summary>
    /// Discard: <see cref="Content"/> is the version that was deleted (or would be, for a dry run), and
    /// <see cref="Changes"/> what it held compared with the version that stays (the published one, else the one before).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Discarded { get; init; }

    /// <summary>The version was scheduled to be published at this time (UTC); nothing went live yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ScheduledFor { get; init; }

    /// <summary>The version was saved for review: its approval sequence started (<c>requestApproval</c>), and nothing went live.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ApprovalRequested { get; init; }

    public bool DryRun { get; init; }

    /// <summary>
    /// False only for a dry run that would fail validation (see <see cref="Validation"/>). A real
    /// write that fails validation returns a <c>validation</c> error (422) instead.
    /// </summary>
    public bool Valid { get; init; } = true;

    /// <summary>Version the change was based on (draft) or the version that was published (publish).</summary>
    public int? BaseVersion { get; init; }

    /// <summary>Every property whose value differs from the base version (or from the type's defaults on create).</summary>
    public IReadOnlyList<PropertyChange> Changes { get; init; } = [];

    /// <summary>Warnings, or for an invalid dry run the errors that would block the save.</summary>
    public IReadOnlyList<ValidationIssue>? Validation { get; init; }

    /// <summary>A create or upload with <see cref="CreateRequest.UpdateExisting"/> found the content and updated it.</summary>
    public bool Existing { get; init; }

    /// <summary>That existing content was in the recycle bin and was moved back under the parent (not for a dry run).</summary>
    public bool Restored { get; init; }

    /// <summary>For an upload: the media type the file is created as (also for a dry run, which has no <see cref="Content"/>).</summary>
    public string? MediaType { get; init; }

    /// <summary>
    /// For a write that publishes: unpublished changes by someone else that it puts live too. A dry run reports them
    /// without failing; a real write only gets this far with <see cref="DraftRequest.IncludeDraft"/>.
    /// </summary>
    public PendingDraft? PendingDraft { get; init; }

    /// <summary>
    /// For a write that published existing content: the version that was published until then, to publish again to go
    /// back. Null when the content (in this language) had never been published.
    /// </summary>
    public int? PreviouslyPublished { get; init; }

    /// <summary>
    /// For a change based on an older version (<see cref="DraftRequest.From"/>, or a ref naming a version): the versions of
    /// the branch saved after <see cref="BaseVersion"/>, newest first. The change doesn't have their changes; they stay as
    /// they are.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LeftOutVersion>? LeftOut { get; init; }

    /// <summary>
    /// The content was saved, but the site's own code failed after that (a handler of the CMS's save or publish events,
    /// such as a search indexer): its message. The save stands; details are in the site's log.
    /// </summary>
    public string? SiteError { get; init; }
}

/// <summary>
/// Changes a publish would put live besides its own: versions saved after the published version, in the language, by
/// someone other than the caller (<see cref="AgentProtocol.PrincipalName"/> for the agent, the editor for the MCP module).
/// </summary>
/// <param name="Version">The newest such version, as <c>id_version</c>.</param>
/// <param name="SavedBy">Who saved it, as the CMS recorded it; empty when nobody was signed in (a scheduled job or import).</param>
/// <param name="Saved">When it was saved, UTC.</param>
/// <param name="Changes">Every property that differs between the published version and the version the publish is based on.</param>
public sealed record PendingDraft(string Version, string? SavedBy, DateTime Saved, IReadOnlyList<PropertyChange> Changes)
{
    /// <summary>The CLI's <c>details.reason</c> for the conflict.</summary>
    public const string Reason = "pendingDraft";

    /// <summary>"changes saved by X in 123_456 (2025-01-31 10:00:00Z) that aren't published yet (Heading, MainArea)".</summary>
    public string Describe()
    {
        var who = string.IsNullOrWhiteSpace(SavedBy) ? "without a user name (a scheduled job or import)" : $"by {SavedBy}";
        var properties = Changes.Count == 0 ? "" : $" ({string.Join(", ", Changes.Select(c => c.Property))})";
        return $"changes saved {who} in {Version} ({Saved.ToUniversalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture)}) that aren't published yet{properties}";
    }
}

/// <summary>A version saved after the one a change was based on, which the change leaves out.</summary>
/// <param name="Version">As <c>id_version</c>.</param>
/// <param name="Status">CMS version status, camelCase, as in <see cref="ContentSummary.Status"/>.</param>
/// <param name="SavedBy">Who saved it, as the CMS recorded it; empty when nobody was signed in (a scheduled job or import).</param>
/// <param name="Saved">When it was saved, UTC.</param>
/// <param name="Primary">It was the primary draft (what edit mode opened) before the change was saved.</param>
public sealed record LeftOutVersion(string Version, string? Status, string? SavedBy, DateTime Saved, bool Primary);

/// <summary>A property value before and after, in the same JSON shape the draft endpoint accepts.</summary>
/// <param name="Before">Omitted when the property was empty.</param>
/// <param name="After">Omitted when the property is now empty.</param>
public sealed record PropertyChange(string Property, JsonElement? Before, JsonElement? After);

/// <summary>Response of <see cref="AgentRoutes.RemoveLanguage"/>.</summary>
/// <param name="Content">The content, in its master language.</param>
/// <param name="Versions">The branch's versions, which are deleted with it.</param>
/// <param name="Published">The branch had a published version, so it was live.</param>
public sealed record RemoveLanguageResult(ContentSummary Content, string Language, int Versions, bool Published, bool Removed, bool DryRun);

/// <summary>Response of move and delete (delete is a move to the recycle bin).</summary>
public sealed record MoveResult
{
    public required ContentSummary Content { get; init; }

    public required string PreviousParent { get; init; }

    public required string Parent { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.Restore"/>.</summary>
public sealed record RestoreResult
{
    /// <summary>The content, after the restore (before it, for a dry run).</summary>
    public required ContentSummary Content { get; init; }

    /// <summary>Where it went (would go, for a dry run).</summary>
    public required string Parent { get; init; }

    /// <summary>The recycle bin, where it was.</summary>
    public required string PreviousParent { get; init; }

    /// <summary>The parent the CMS stored when it was deleted; null when it has none (the request then named one).</summary>
    public string? StoredParent { get; init; }

    /// <summary>False for a dry run.</summary>
    public bool Restored { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.RestoreParents"/>.</summary>
/// <param name="Parents">Content id to the parent the CMS stored for it (a ref, <c>123</c>); items without one are left out.</param>
public sealed record RestoreParentsResult(IReadOnlyDictionary<string, string> Parents)
{
    /// <summary>Ids per request: they go in the query string, which Kestrel limits to 8 KB with the rest of the request line.</summary>
    public const int MaxIds = 500;
}
