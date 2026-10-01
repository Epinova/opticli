using System.Globalization;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// The agent's <see cref="WriteResult"/> in the CLI's identity conventions: <see cref="Ref"/> is the
/// content (pass it to any command), <see cref="Version"/> the version written (<c>id_version</c>).
/// </summary>
/// <param name="Version">The saved version; for a dry run the version the change was applied to.</param>
/// <param name="BaseVersion">The version the change was based on (draft) or that was published (publish).</param>
public sealed record WriteOutput(
    string? Ref,
    string? Version,
    Guid? Guid,
    string? Type,
    string? Name,
    string? Language,
    string? Status,
    string? Parent,
    bool Saved,
    bool Published,
    bool DryRun,
    bool Valid,
    string? BaseVersion,
    IReadOnlyList<PropertyChange> Changes,
    IReadOnlyList<ValidationIssue>? Validation)
{
    /// <summary>For an upload: the file sent, and where the site stored it (none for a dry run).</summary>
    public UploadInfo? Upload { get; init; }

    /// <summary>True when a create, block, upload or translate step of <c>apply --update-existing</c> updated existing content.</summary>
    public bool? Existing { get; init; }

    /// <summary>True when that content was in the recycle bin and was moved back.</summary>
    public bool? Restored { get; init; }

    /// <summary>
    /// For a write that publishes: changes someone else saved after the published version, which it puts live too (a dry
    /// run reports them; a real write needs <see cref="WriteOperation.IncludeDraft"/>).
    /// </summary>
    public PendingDraft? PendingDraft { get; init; }

    /// <summary>
    /// For a write that published existing content: the version that was published until then (<c>id_version</c>), to
    /// publish again to go back. Null after the first publish of the content in its language.
    /// </summary>
    public string? PreviouslyPublished { get; init; }

    /// <summary>True when unpublish took the branch offline; <see cref="PreviouslyPublished"/> is the version that was live.</summary>
    public bool? Unpublished { get; init; }

    /// <summary>True when discard deleted the version (<see cref="Version"/>); <see cref="Changes"/> are what it held.</summary>
    public bool? Discarded { get; init; }

    /// <summary>For <c>translate --with-blocks</c>: what happened to each block of the "For this page" folder.</summary>
    public IReadOnlyList<BlockTranslation>? Blocks { get; init; }

    /// <summary>When a scheduled publish (<c>--publish-at</c>) puts the version live, UTC; until then it is <c>delayedPublish</c>.</summary>
    public DateTime? ScheduledFor { get; init; }

    /// <summary>True when the version was sent for review: its approval sequence started, and nothing went live.</summary>
    public bool? ApprovalRequested { get; init; }

    /// <summary>The site's own code failed after the save (a handler of the CMS's events): its message. The save stands.</summary>
    public string? SiteError { get; init; }

    /// <param name="type">Shown when the agent returns no content (a dry-run create).</param>
    public static WriteOutput From(WriteResult result, string? type = null, string? name = null, string? parent = null)
    {
        var content = result.Content;
        return new WriteOutput(
            content is null ? null : Id(content.Id),
            content?.Version is { } version ? VersionRef(content.Id, version) : null,
            content?.Guid,
            content?.Type ?? type,
            content?.Name ?? name,
            content?.Language,
            content?.Status,
            content?.Parent ?? parent,
            result.Saved,
            result.Published,
            result.DryRun,
            result.Valid,
            content is not null && result.BaseVersion is { } baseVersion ? VersionRef(content.Id, baseVersion) : null,
            result.Changes,
            result.Validation)
        {
            PendingDraft = result.PendingDraft,
            PreviouslyPublished = content is not null && result.PreviouslyPublished is { } previous ? VersionRef(content.Id, previous) : null,
            SiteError = result.SiteError,
            ApprovalRequested = result.ApprovalRequested ? true : null,
            ScheduledFor = result.ScheduledFor,
            Unpublished = result.Unpublished ? true : null,
            Discarded = result.Discarded ? true : null,
        };
    }

    public static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    public static string VersionRef(int id, int version) => $"{Id(id)}_{version.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A block of the content's "For this page" folder that <c>translate --with-blocks</c> gave the branch.</summary>
/// <param name="Version">The new branch's version; for an unchanged or skipped block, null.</param>
/// <param name="Status"><c>translated</c>, <c>exists</c> (it had the branch), or <c>notLocalizable</c>.</param>
public sealed record BlockTranslation(string Ref, string? Name, string? Type, string? Version, string Status);

/// <summary>Result of <c>translate --remove</c>.</summary>
/// <param name="Versions">The branch's versions, deleted with it.</param>
/// <param name="Published">The branch was live.</param>
public sealed record RemoveLanguageOutput(string Ref, Guid Guid, string? Type, string? Name, string Language, int Versions, bool Published, bool Removed, bool DryRun);

/// <param name="File">The local file that was uploaded.</param>
/// <param name="Blob">Where the site stored it, as <c>opticli blob</c> shows it; null for a dry run.</param>
public sealed record UploadInfo(string File, long Bytes, Queries.BlobLocation? Blob);

/// <summary>Result of move and delete (a delete is a move to the recycle bin).</summary>
/// <param name="Moved">False for a dry run.</param>
/// <param name="Descendants">Content items below it, which moved with it.</param>
public sealed record MoveOutput(
    string Ref,
    Guid? Guid,
    string? Type,
    string? Name,
    string? Language,
    string? Status,
    string? Parent,
    string? PreviousParent,
    bool Moved,
    bool DryRun,
    int Descendants,
    bool? RecycleBin = null)
{
    /// <summary>
    /// For a delete: references to it or its descendants from other content, which would point into the recycle bin
    /// (the first <see cref="Queries.IncomingReferences.Max"/>); null when there are none.
    /// </summary>
    public IReadOnlyList<Queries.IncomingReference>? References { get; init; }

    /// <summary>How many <see cref="References"/> there are in all.</summary>
    public int? ReferenceCount { get; init; }

    public MoveOutput WithReferences(Queries.IncomingReferences found) => found.Count == 0
        ? this
        : this with { References = found.References, ReferenceCount = found.Count };
}

/// <summary><c>details</c> of a delete stopped because other content references what it deletes.</summary>
public sealed record ReferencedDetails(string Reason, IReadOnlyList<Queries.IncomingReference> References, int Count);

/// <summary>Result of <c>access</c>: the item's effective ACL before and after.</summary>
/// <param name="Saved">False for a dry run, and when the change was already in place.</param>
public sealed record AccessOutput(
    string Ref,
    Guid Guid,
    string? Type,
    string? Name,
    bool Saved,
    bool DryRun,
    AccessList Before,
    AccessList After);

/// <param name="Output">A <see cref="WriteOutput"/>, <see cref="MoveOutput"/> or <see cref="AccessOutput"/>.</param>
/// <param name="Source"><c>agent</c> when the site did the work, <c>db</c> for dry runs opticli checks itself.</param>
/// <param name="CreatedId">Content id of what a create made (for plans' <c>$id</c>).</param>
public sealed record WriteOutcome(object Output, string Source, int? CreatedId, IReadOnlyList<string> Warnings);
