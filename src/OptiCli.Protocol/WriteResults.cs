using System.Text.Json;

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
    /// The content was saved, but the site's own code failed after that (a handler of the CMS's save or publish events,
    /// such as a search indexer): its message. The save stands; details are in the site's log.
    /// </summary>
    public string? SiteError { get; init; }
}

/// <summary>
/// Changes a publish would put live besides its own: versions saved after the published version, in the language, by
/// someone other than <see cref="AgentProtocol.PrincipalName"/>.
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

/// <summary>A property value before and after, in the same JSON shape the draft endpoint accepts.</summary>
/// <param name="Before">Omitted when the property was empty.</param>
/// <param name="After">Omitted when the property is now empty.</param>
public sealed record PropertyChange(string Property, JsonElement? Before, JsonElement? After);

/// <summary>Response of move and delete (delete is a move to the recycle bin).</summary>
public sealed record MoveResult
{
    public required ContentSummary Content { get; init; }

    public required string PreviousParent { get; init; }

    public required string Parent { get; init; }
}
