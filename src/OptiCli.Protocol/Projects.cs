using System.Text.Json.Serialization;

namespace OptiCli.Protocol;

/// <summary>Body of <see cref="AgentRoutes.Projects"/>: a new, active project (edit mode's Projects).</summary>
public sealed record ProjectCreateRequest
{
    /// <summary>
    /// At most <see cref="MaxNameLength"/> characters. Refused (<c>conflict</c>) when a project that isn't published has
    /// the name already: projects are named by their name too, and the CMS doesn't keep names unique.
    /// </summary>
    public required string Name { get; init; }

    public bool DryRun { get; init; }

    /// <summary>The CMS stores a project's name in 255 characters.</summary>
    public const int MaxNameLength = 255;
}

/// <summary>
/// Body of <see cref="AgentRoutes.ProjectReady"/>: mark items Ready to publish, as the edit UI's "Ready to Publish" does
/// (<c>SaveAction.CheckIn</c> of the item's version, which keeps its version id). Give <see cref="Items"/> or
/// <see cref="All"/>.
/// </summary>
/// <remarks>
/// Where an approval sequence applies, marking ready would skip the reviewers, so it is refused (<c>refused</c>,
/// <see cref="AgentErrorReasons.ApprovalSequence"/>) unless <see cref="RequestApproval"/>, which sends the item for review
/// instead (as the edit UI's "Ready for Review"). Every item is checked before any is changed: one that can't be marked
/// ready stops the request, with each problem in <see cref="AgentError.Project"/>.
/// </remarks>
public sealed record ProjectReadyRequest
{
    /// <summary>
    /// The items, as content refs: <c>123</c> is every item of that content (in <see cref="Lang"/>, if given), <c>123_456</c>
    /// the item of that version. Each must be in the project.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>Only the items in this language.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lang { get; init; }

    /// <summary>
    /// Every item that is a draft (<c>checkedOut</c> or <c>rejected</c>); the others (ready already, in review, published,
    /// scheduled, in the recycle bin) are left as they are and listed as <see cref="ProjectItemOutcomes.Skipped"/>, and
    /// drafts the caller may not edit (Edit access, or the language) as <see cref="ProjectItemOutcomes.NoAccess"/>.
    /// </summary>
    public bool All { get; init; }

    /// <summary>
    /// Also mark ready (or send for review) items whose version someone else saved (<see cref="ProjectItemResult.PendingDraft"/>).
    /// The CMS records whoever marks a version ready as the one who saved it, so marking another person's draft ready makes
    /// their changes the caller's, and a publish no longer asks about them: without this, such an item is a
    /// <c>conflict</c> (<see cref="AgentErrorReasons.PendingDraft"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeDraft { get; init; }

    /// <summary>
    /// Where an approval sequence applies, send the item for review (<c>SaveAction.RequestApproval</c>) instead of marking it
    /// ready; items without one are marked ready as usual.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequestApproval { get; init; }

    /// <summary>
    /// With <see cref="RequestApproval"/>: the comment for the reviewers, added to each review it starts. Required where
    /// the sequence says so (its "require a comment" setting).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.ProjectRemove"/>: take items out of the project. Their versions stay as they are.</summary>
public sealed record ProjectRemoveRequest
{
    /// <summary>As <see cref="ProjectReadyRequest.Items"/>.</summary>
    public required IReadOnlyList<string> Items { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lang { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>
/// Body of <see cref="AgentRoutes.ProjectPublish"/> (may be empty): publish, or schedule, the items marked Ready to
/// publish, through the CMS's <c>ProjectPublisher</c>. Items that aren't ready stay as they are (<see cref="ProjectResult.NotReady"/>).
/// </summary>
/// <remarks>
/// Every ready item is checked first, as a single publish is (read and language access, validation, the publishing
/// gate, served media, an approval sequence's approval, other people's drafts): one that fails stops the whole publish,
/// with each problem in <see cref="AgentError.Project"/>, and nothing goes live.
/// </remarks>
public sealed record ProjectPublishRequest
{
    /// <summary>Schedule the publish for this time (UTC) instead: the items become <c>delayedPublish</c>, the project <c>delayedPublished</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PublishAt { get; init; }

    /// <summary>
    /// Also publish the unpublished changes of others that ready versions carry (<see cref="PendingDraft"/>, per item).
    /// Without it such a publish fails with <c>conflict</c> (<see cref="AgentErrorReasons.PendingDraft"/>); a dry run lists them.
    /// Not needed for the changes of the person who marked the item ready (<see cref="ProjectItemResult.ReadyBy"/>): marking
    /// an item Ready to publish is their consent to publish it, their own earlier drafts in it included.
    /// </summary>
    public bool IncludeDraft { get; init; }

    public bool DryRun { get; init; }

    /// <summary>
    /// As <see cref="PublishRequest.WithBlocks"/>, for the ready items: blocks of a ready item's own "For this page" folder
    /// that it shows, whose drafts go live with it although they have a published version already. A block that is an
    /// item of the project follows the project's rules either way.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? WithBlocks { get; init; }

    /// <summary>As <see cref="PublishRequest.WithoutBlocks"/>, for the ready items.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? WithoutBlocks { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.ProjectReactivate"/> (may be empty): cancel a scheduled project's publish.</summary>
public sealed record ProjectReactivateRequest
{
    public bool DryRun { get; init; }
}

/// <summary>A project, as a project operation leaves it.</summary>
public sealed record ProjectSummary
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    /// <summary>One of <see cref="ProjectStatuses"/>.</summary>
    [OutputValues(typeof(ProjectStatuses), Open = true)]
    public required string Status { get; init; }

    /// <summary>For a scheduled project (<c>delayedPublished</c>), when it is published, UTC.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ScheduledFor { get; init; }

    public DateTime? Created { get; init; }

    public string? CreatedBy { get; init; }

    /// <summary>Its items (a version of one content item in one language each).</summary>
    public int Items { get; init; }

    /// <summary>Of those, the ones marked Ready to publish (<c>checkedIn</c>, not in the recycle bin): what a publish puts live.</summary>
    public int Ready { get; init; }
}

/// <summary>A project's status, as the CMS's <c>ProjectStatus</c>, camelCase.</summary>
[ValueSetName("ProjectStatus")]
public static class ProjectStatuses
{
    public const string Active = "active";
    public const string Publishing = "publishing";
    public const string PublishFailed = "publishFailed";
    public const string Published = "published";
    public const string DelayedPublished = "delayedPublished";
    public const string Reactivating = "reactivating";
}

/// <summary>Which project UI the site's CMS gives editors (<see cref="ProjectResult.EditorUi"/>).</summary>
public static class ProjectEditorUi
{
    /// <summary>Project mode: the project bar, where an editor's saves go into the project they picked.</summary>
    public const string ProjectMode = "projectMode";

    /// <summary>
    /// Project mode off, the Projects gadget on (CMS 13's default): editors see projects and their items there, and its
    /// Publish puts every unpublished item live, ready or not.
    /// </summary>
    public const string Gadget = "gadget";

    /// <summary>
    /// Neither (both turned off in the site's configuration): editors have no projects, and saves into one are refused.
    /// Only reads (<see cref="ProjectList"/>, <see cref="ProjectDetails"/>) say so; a write is refused instead.
    /// </summary>
    public const string None = "none";
}

/// <summary>The projects the MCP module's <c>list_projects</c> shows, newest first.</summary>
public sealed record ProjectList
{
    public required IReadOnlyList<ProjectSummary> Projects { get; init; }

    /// <summary>As <see cref="ProjectResult.EditorUi"/>, or <see cref="ProjectEditorUi.None"/>.</summary>
    public string? EditorUi { get; init; }

    /// <summary>More projects than the limit matched.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool More { get; init; }
}

/// <summary>One project and its items, as the MCP module's <c>get_project</c> shows it.</summary>
public sealed record ProjectDetails
{
    public required ProjectSummary Project { get; init; }

    /// <summary>As <see cref="ProjectResult.EditorUi"/>, or <see cref="ProjectEditorUi.None"/>.</summary>
    public string? EditorUi { get; init; }

    /// <summary>Its items, in the project's order: for an editor, the ones whose content they can read.</summary>
    public required IReadOnlyList<ProjectItemEntry> Items { get; init; }
}

/// <summary>An item of a project: its version, and where it stands.</summary>
/// <param name="State">One of <see cref="ProjectItemStates"/>.</param>
public sealed record ProjectItemEntry(ContentSummary Content, string State);

/// <summary>Where an item of a project stands (<see cref="ProjectItemEntry.State"/>).</summary>
public static class ProjectItemStates
{
    /// <summary>A draft (<c>checkedOut</c>), or rejected by a reviewer: not ready.</summary>
    public const string Draft = "draft";

    /// <summary>Marked Ready to publish (<c>checkedIn</c>): what a project publish puts live.</summary>
    public const string Ready = "ready";

    /// <summary>Awaiting approval: its reviewers decide.</summary>
    public const string InReview = "inReview";

    /// <summary>Its version is scheduled to be published.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Its version is the published one, or was.</summary>
    public const string Published = "published";

    /// <summary>The content is in the recycle bin.</summary>
    public const string Deleted = "deleted";

    /// <summary>The version no longer exists.</summary>
    public const string Missing = "missing";
}

/// <summary>A project that holds a version of content (the MCP module's <c>get_content</c>, as the CLI's <c>get</c> shows it).</summary>
/// <param name="Status">One of <see cref="ProjectStatuses"/>.</param>
/// <param name="Version">The version it holds (<c>id_version</c>); null when the item names the content without one.</param>
/// <param name="Language">The item's language; null for content without languages.</param>
public sealed record ContentProject(int Id, string Name, string Status, string? Version, string? Language);

/// <summary>What happened (or, for a dry run, would happen) to one item: <see cref="ProjectItemResult.Outcome"/>.</summary>
[ValueSetName("ProjectItemOutcome")]
public static class ProjectItemOutcomes
{
    /// <summary>Marked Ready to publish.</summary>
    public const string Ready = "ready";

    /// <summary>Sent for review: its approval sequence started.</summary>
    public const string ApprovalRequested = "approvalRequested";

    /// <summary>Ready already; left as it is.</summary>
    public const string AlreadyReady = "alreadyReady";

    /// <summary>Left as it is: <see cref="ProjectReadyRequest.All"/> takes only drafts.</summary>
    public const string Skipped = "skipped";

    /// <summary>
    /// Left as it is: with <see cref="ProjectReadyRequest.All"/>, a draft the caller may not edit (Edit access, or the
    /// language); <see cref="ProjectItemResult.Validation"/> says why. Named in <see cref="ProjectReadyRequest.Items"/>, it is a
    /// problem instead.
    /// </summary>
    public const string NoAccess = "noAccess";

    /// <summary>Taken out of the project.</summary>
    public const string Removed = "removed";

    /// <summary>Published by the project's publish.</summary>
    public const string Published = "published";

    /// <summary>Scheduled by the project's scheduled publish.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Not marked ready, so the publish left it as it is.</summary>
    public const string NotReady = "notReady";

    /// <summary>Its version is published already (or was, before a newer one), so the publish left it.</summary>
    public const string AlreadyPublished = "alreadyPublished";

    /// <summary>Its scheduled publish was cancelled (reactivate): it is a draft again, not ready.</summary>
    public const string Unscheduled = "unscheduled";

    /// <summary>The CMS refused or failed it; <see cref="ProjectItemResult.Error"/> says how.</summary>
    public const string Failed = "failed";
}

/// <summary>One item of a project, and what a project operation did to it.</summary>
public sealed record ProjectItemResult
{
    /// <summary>The item's version, as it is now (after the operation; before it, for a dry run).</summary>
    public required ContentSummary Content { get; init; }

    /// <summary>
    /// One of <see cref="ProjectItemOutcomes"/>. For an item with a <see cref="Problem"/>: what the operation would do with it
    /// (a publish's), else null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [OutputValues(typeof(ProjectItemOutcomes))]
    public string? Outcome { get; init; }

    /// <summary>
    /// Why the operation can't go ahead for this item, found before anything was changed: the request is refused with
    /// every item's problem (for a dry run, <see cref="ProjectResult.Valid"/> is false).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectItemProblem? Problem { get; init; }

    /// <summary>For <see cref="ProjectItemOutcomes.Failed"/>: what the CMS said. Items before and after it went ahead.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>Warnings, or the validation errors behind a <see cref="Problem"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ValidationIssue>? Validation { get; init; }

    /// <summary>
    /// For a publish: other people's unpublished changes the ready version carries, which go live with it; the ones that
    /// need <see cref="ProjectPublishRequest.IncludeDraft"/> when there are any, else those of <see cref="ReadyBy"/>, which
    /// don't. For marking ready: the item's version is another person's draft (<see cref="ProjectReadyRequest.IncludeDraft"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingDraft? PendingDraft { get; init; }

    /// <summary>
    /// For a publish: who marked the item's version Ready to publish, when it wasn't the caller (the CMS's "status changed
    /// by"). Marking it ready was their consent to publish it, so their own unpublished changes in it go live without
    /// <see cref="ProjectPublishRequest.IncludeDraft"/>; another person's older draft it carries still needs it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReadyBy { get; init; }

    /// <summary>For a published item: the version that was live until then, to publish again to go back. Null when it never was.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PreviouslyPublished { get; init; }

    /// <summary>
    /// For a publish: the drafts of shared blocks in the item's own "For this page" folder that its published version's
    /// areas show, published (scheduled) along with it, as a single publish does (<see cref="WriteResult.SharedBlocks"/>).
    /// For a dry run, those it would publish. Null when there are none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SharedBlockResult>? SharedBlocks { get; init; }
}

/// <summary>Why an item can't take a project operation: the error it would get on its own.</summary>
/// <param name="Code">One of <see cref="AgentErrorCodes"/>.</param>
/// <param name="Reason">One of <see cref="AgentErrorReasons"/>, when there is one.</param>
public sealed record ProjectItemProblem(string Code, string Message, string? Reason = null);

/// <summary>Response of every project route, and <see cref="AgentError.Project"/> for one refused over its items.</summary>
public sealed record ProjectResult
{
    /// <summary>The project after the operation (before it, for a dry run; as it would be made, for a dry-run create, with id 0).</summary>
    public required ProjectSummary Project { get; init; }

    /// <summary>
    /// Which project UI editors have on this site: <see cref="ProjectEditorUi.ProjectMode"/> or
    /// <see cref="ProjectEditorUi.Gadget"/>; null when it can't be told (a site without the CMS edit UI).
    /// </summary>
    [OutputValues(typeof(ProjectEditorUi))]
    public string? EditorUi { get; init; }

    public bool DryRun { get; init; }

    /// <summary>False only for a dry run that would be refused: see each item's <see cref="ProjectItemResult.Problem"/>.</summary>
    public bool Valid { get; init; } = true;

    /// <summary>A create made the project.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Created { get; init; }

    /// <summary>A delete removed the project; its items' versions stay as they were.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deleted { get; init; }

    /// <summary>A reactivate cancelled the scheduled publish: the project is active again, its scheduled items drafts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Reactivated { get; init; }

    /// <summary>Ready, remove, reactivate and delete: the items, with what happened to each.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectItemResult>? Items { get; init; }

    /// <summary>A publish: the items it put live (would put live, for a dry run).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectItemResult>? Published { get; init; }

    /// <summary>A scheduled publish: the items it scheduled, for <see cref="ScheduledFor"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectItemResult>? Scheduled { get; init; }

    /// <summary>A publish: the ready items the CMS failed to publish (the project is then <c>publishFailed</c>, and publishes again).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectItemResult>? Failed { get; init; }

    /// <summary>A publish: the items left as they were because they aren't ready (drafts, in review, rejected, scheduled, in the recycle bin).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectItemResult>? NotReady { get; init; }

    /// <summary>A publish: the items whose version is live already (or was), which it leaves as they are.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectItemResult>? AlreadyPublished { get; init; }

    /// <summary>A scheduled publish: when, UTC.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ScheduledFor { get; init; }

    /// <summary>
    /// A publish the CMS failed part of: its message (the project is <c>publishFailed</c>). <see cref="Published"/> lists
    /// what went live all the same, <see cref="Failed"/> what didn't.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SiteError { get; init; }

    /// <summary>
    /// A publish: what the site's own code changed while the items were published, as <see cref="WriteResult.SiteChanges"/>
    /// (the items' own publish aside). Recorded from the CMS's background task that publishes them; work a site's handler
    /// moves to yet another thread isn't seen.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SiteChange>? SiteChanges { get; init; }

    /// <summary>Warnings about the operation as a whole (what to do next after a reactivate, say).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ValidationIssue>? Validation { get; init; }
}

/// <summary>What a save with <see cref="DraftRequest.Project"/> (and the other save requests' <c>project</c>) did to the project.</summary>
/// <param name="Id">The project's id.</param>
public sealed record ProjectSaveResult(int Id, string Name)
{
    /// <summary>One of <see cref="ProjectSaveItems"/>; for a dry run, what would happen.</summary>
    public required string Item { get; init; }

    /// <summary>For <see cref="ProjectSaveItems.Replaced"/>: the version the project held until then (<c>id_version</c>), which stays as it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Replaced { get; init; }

    /// <summary>The saved version is marked Ready to publish (would be, for a dry run).</summary>
    public bool Ready { get; init; }

    /// <summary>As <see cref="ProjectResult.EditorUi"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [OutputValues(typeof(ProjectEditorUi))]
    public string? EditorUi { get; init; }

    /// <summary>
    /// The content was saved, but putting it in the project, or marking it ready, failed after that: the CMS's message.
    /// The save stands; the project is as <see cref="Item"/> and <see cref="Ready"/> say.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>Values of <see cref="ProjectSaveResult.Item"/>.</summary>
public static class ProjectSaveItems
{
    /// <summary>The saved version is a new item of the project.</summary>
    public const string Added = "added";

    /// <summary>The saved version took the place of the content's earlier version in the project, in the same language.</summary>
    public const string Replaced = "replaced";

    /// <summary>The project holds that version already (a save into the same draft, or nothing to save).</summary>
    public const string Unchanged = "unchanged";

    /// <summary>Nothing was saved and the version the change was based on isn't a draft, so nothing was put in the project.</summary>
    public const string None = "none";
}
