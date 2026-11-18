using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// What create, draft and languages share: apply the request to a writable instance, diff it against
/// its starting point, validate, then either report (dry run) or save.
/// </summary>
internal sealed class WriteFlow
{
    public WriteFlow(CmsCall call)
        : this(call, Compat.InlineBlocks.EditUiCreatesThem(call.Services) == false)
    {
    }

    /// <param name="sharedBlocks">
    /// New blocks in ContentAreas are shared ones in the content's "For this page" folder, as the site's edit UI makes them
    /// (<see cref="Compat.InlineBlocks.EditUiCreatesThem"/> is false); else inline, as where it makes those, or can't be
    /// asked. Tests set it; a site's write asks the site.
    /// </param>
    internal WriteFlow(CmsCall call, bool sharedBlocks)
    {
        Call = call;
        Types = call.Service<IContentTypeRepository>();
        Versions = call.Service<IContentVersionRepository>();
        Locator = new ContentLocator(call);
        Writer = new PropertyWriter(call, Locator, new BlockFactory(
            call.Service<EPiServer.Construction.IContentDataFactory<BlockData>>(), call.Service<EPiServer.Construction.IContentDataBuilder>(), Types),
            call.Service<CategoryRepository>(), call.Service<IFrameRepository>(), call.Service<EPiServer.Web.DisplayOptions>(), sharedBlocks);
        Areas = new AreaEditor(Locator, Writer);
    }

    /// <summary>Who the write is for; every save, move and delete goes through it.</summary>
    public CmsCall Call { get; }

    /// <summary>Signalled when the caller gave up (a timeout or Ctrl+C in the CLI): nothing is saved after that.</summary>
    public CancellationToken Aborted => Call.Aborted;

    public ContentLocator Locator { get; }

    public PropertyWriter Writer { get; }

    public AreaEditor Areas { get; }

    public IContentTypeRepository Types { get; }

    public IContentVersionRepository Versions { get; }

    public IContentRepository Repository => Locator.Repository;

    /// <summary>
    /// The file an upload attaches to the media it saves: its extension, and its bytes (null for a dry run without
    /// them). Null for any other write, which keeps the stored file.
    /// </summary>
    public (string Extension, byte[]? Data)? NewFile { get; set; }

    /// <summary>
    /// The project the save goes into (the request's <c>project</c>, <see cref="ProjectSave.For"/>); null for a save
    /// without one. Set before <see cref="Decide"/>, which it changes for <c>ready</c>.
    /// </summary>
    public ProjectSave? Project { get; set; }

    /// <summary>
    /// The write's publish puts other people's drafts of the content live too, which the caller confirmed
    /// (<c>includeDraft</c>): their drafts of the blocks of its "For this page" folder go along as well
    /// (<see cref="SharedAreaBlocks.PublishAlong"/>).
    /// </summary>
    public bool OthersDraftsConfirmed { get; set; }

    /// <summary>
    /// Where the site's edit UI makes new blocks in a ContentArea shared: after a save that publishes (or schedules) the
    /// content, the drafts of its own folder's blocks its areas show, published along (<see cref="SharedAreaBlocks.PublishAlong"/>);
    /// null where it makes them inline, which is as before.
    /// </summary>
    /// <param name="at">For a schedule: when; else the published version's start-publish date, which a scheduled save sets.</param>
    /// <param name="inProject">For a project publish: the project's own items, which are left to it (<see cref="SharedAreaBlocks.PublishAlong"/>).</param>
    public List<SharedBlockResult>? PublishAlong(IContent published, SaveAction action, bool othersConfirmed, bool dryRun, List<ValidationIssue> warnings,
        Func<int, System.Globalization.CultureInfo?, bool?>? inProject = null, DateTime? at = null) =>
        Writer.Shared is { } shared && Kind(action) is SaveAction.Publish or SaveAction.Schedule
            ? shared.PublishAlong(published, Kind(action), at ?? (published as IVersionable)?.StartPublish, othersConfirmed, dryRun, warnings, inProject)
            : null;

    /// <summary>
    /// Where the site's edit UI makes new blocks in an area shared: a review request (an approval sequence's) of
    /// <paramref name="version"/> is refused while the version shows blocks of its own "For this page" folder that aren't
    /// published (or that this write makes or changes). The approval publishes the content alone, so it would go live
    /// showing them as they were, or not at all. In a CMS project the content and its blocks are reviewed and published
    /// together, so a write with one isn't refused.
    /// </summary>
    /// <exception cref="AgentException"><c>usage</c>, with how to go about it instead; also for a dry run.</exception>
    public void RequireBlocksPublishedForReview(IContent version, SaveAction action, string what)
    {
        if (Kind(action) != SaveAction.RequestApproval || Project is not null || Writer.Shared is not { } shared
            || shared.UnpublishedForReview(version) is not { Count: > 0 } unpublished)
        {
            return;
        }
        throw AgentException.Usage(
            $"{what} would go for review while its ContentAreas show blocks of its \"For this page\" folder that aren't published: {string.Join(", ", unpublished)}. The approval publishes the content alone, so it would go live without those blocks' changes.",
            Call.ForCaller(
                "Save the change as a draft first (without --request-approval), publish those blocks (sharedBlocks gives their refs: opticli publish <ref>, with --request-approval where they need approval too), then send the content for review; or save the content and its blocks in a CMS project (--in-project <project> --ready --request-approval) so they are reviewed and published together. Nothing was saved.",
                "Save the change as a draft first (without requestApproval), publish those blocks (sharedBlocks gives their refs: publish_content on each, with requestApproval where they need approval too), then send the content for review; or save the content and its blocks in a CMS project (project, ready and requestApproval) so they are reviewed and published together. Nothing was saved."));
    }

    /// <summary>New content as stored, if a save got that far.</summary>
    public IContent? Saved(IContent writable) =>
        writable.ContentGuid != Guid.Empty && Repository.TryGet<IContent>(writable.ContentGuid, out var stored) ? stored : null;

    /// <param name="action">From <see cref="Approvals.Decide"/>: null saves a draft.</param>
    public static SaveAction DraftAction(SaveAction? action) => (action ?? SaveAction.Save) | SaveAction.ForceNewVersion;

    /// <summary>
    /// How a write that may publish saves: <see cref="Approvals.Decide"/>, or <see cref="SaveAction.Schedule"/> for a
    /// publish at a later time, to which the same rules apply.
    /// </summary>
    /// <exception cref="AgentException"><c>usage</c> for a time that isn't in the future, or scheduled with requestApproval.</exception>
    public static SaveAction? Publishing(CmsCall call, ContentReference link, bool publish, bool requestApproval, DateTime? publishAt, string what)
    {
        if (publishAt is not { } at)
        {
            return Approvals.Decide(call, link, publish, requestApproval, what);
        }
        if (requestApproval)
        {
            throw AgentException.Usage("A review request can't be scheduled: the reviewers decide when it is published.", "Give publishAt or requestApproval, not both.");
        }
        if (at.ToUniversalTime() <= DateTime.UtcNow)
        {
            throw AgentException.Usage($"publishAt ({at.ToUniversalTime():u}) isn't in the future.", "Publish it now (publish) instead, or give a later time.");
        }
        Approvals.Decide(call, link, publish: true, requestApproval: false, what);
        return SaveAction.Schedule;
    }

    /// <summary>
    /// How this write saves: <see cref="Publishing"/>, after <see cref="Project"/> has had its say on
    /// <paramref name="requestApproval"/> (<see cref="ProjectSave.RequestApproval"/>: with <c>ready</c>, a review where
    /// an approval sequence applies, else none).
    /// </summary>
    /// <param name="link">The content, or the parent of new content.</param>
    public SaveAction? Decide(ContentReference link, bool publish, bool requestApproval, DateTime? publishAt, string what) =>
        Publishing(Call, link, publish, Project?.RequestApproval(link, requestApproval, what) ?? requestApproval, publishAt, what);

    /// <summary>For a scheduled save: the version's start-publish date, which the CMS publishes it at.</summary>
    public static void ScheduleAt(IContent writable, SaveAction? action, DateTime? publishAt)
    {
        if (action is not { } scheduled || Kind(scheduled) != SaveAction.Schedule)
        {
            return;
        }
        if (writable is not IVersionable versionable)
        {
            throw AgentException.Usage($"Content {writable.ContentLink.ID} has no versions, so its publish can't be scheduled.");
        }
        versionable.StartPublish = publishAt!.Value.ToLocalTime();
    }

    /// <summary>
    /// A publish of a version whose stop-publish date has passed (a draft saved after an unpublish carries it) would put
    /// it live already expired, so nothing would show; the date is cleared, unless the request sets it.
    /// </summary>
    /// <returns>A warning saying so; null when nothing was cleared.</returns>
    public static ValidationIssue? ClearExpiredStopPublish(IContent writable, SaveAction? action, IReadOnlyDictionary<string, JsonElement>? properties)
    {
        if (action is not { } publishing || Kind(publishing) is not (SaveAction.Publish or SaveAction.Schedule) || writable is not IVersionable { StopPublish: { } stop } versionable || stop > DateTime.Now
            || properties?.Keys.Any(k => PropertyWriter.PublishDates.TryGetValue(k, out var date) && date == PropertyWriter.StopPublishKey) == true)
        {
            return null;
        }
        versionable.StopPublish = null;
        return new ValidationIssue(PropertyWriter.StopPublishKey,
            $"Its stop-publish date ({stop.ToUniversalTime():u}) had passed, which would have kept it offline, so it was cleared.", "warning");
    }

    /// <summary>The action without its flags (<c>ForceNewVersion</c>, ...).</summary>
    public static SaveAction Kind(SaveAction action) => action & SaveAction.ActionMask;

    /// <summary>
    /// Stops a write whose caller has gone: the CLI reports such a step as possibly saved, so it must not be saved
    /// after the CLI stopped waiting. Checked just before the save, which itself can't be interrupted.
    /// </summary>
    public void ThrowIfAborted()
    {
        if (Aborted.IsCancellationRequested)
        {
            throw new AgentException(AgentErrorCodes.Internal, "The caller stopped waiting before the save, so nothing was saved.", "the CLI timed out or was interrupted");
        }
    }

    /// <param name="writable">The instance with the request applied.</param>
    /// <param name="before">Snapshot of the starting point (base version, master language, or type defaults).</param>
    /// <param name="shown">What to report as the content for a dry run or a no-op.</param>
    /// <param name="saveUnchanged">Save even when nothing changed (new content, new branches, publish).</param>
    /// <param name="precheck">Issues found before CMS validation (e.g. the type isn't allowed under the parent).</param>
    /// <param name="beforeSave">
    /// Runs once everything is checked, right before a real save or in place of it when nothing changed (e.g. restoring
    /// the content from the recycle bin).
    /// </param>
    /// <param name="intoVersion">
    /// The version <paramref name="action"/> saves into instead of a new one (<see cref="SaveInto"/>): the one
    /// <paramref name="writable"/> is a clone of. The result's <see cref="WriteResult.IntoVersion"/> says whether it did.
    /// </param>
    public WriteResult Save(
        IContent writable,
        IReadOnlyDictionary<string, JsonElement?> before,
        SaveAction action,
        bool dryRun,
        ContentSummary? shown,
        int? baseVersion,
        bool saveUnchanged,
        IEnumerable<ValidationIssue>? precheck = null,
        Action? beforeSave = null,
        int? intoVersion = null)
    {
        // For an editor: edit access to the language, and the publish gate for a save that would put something live, are
        // checked for a dry run too, so the assistant learns before it tries.
        Call.RequireLanguageAccess(writable);
        Call.RequirePublishing(action, writable);
        if (!Call.MayWriteScript && writable is MediaData media)
        {
            // Media is served by its URL, whose last segment is made from its name: that extension counts as the file's.
            // Compared with what visitors get unless the save changes it: the version saved over for a draft, the
            // published one for a save that publishes or schedules (the version saved over may be a draft saved elsewhere).
            var goesLive = Kind(action) is SaveAction.Publish or SaveAction.Schedule;
            var unchanged = ContentReference.IsNullOrEmpty(media.ContentLink) ? null
                : goesLive ? Versions.LoadPublished(media.ContentLink.ToReferenceWithoutVersion()) is { } live && Repository.TryGet<IContent>(live.ContentLink, out var liveVersion) ? liveVersion : null
                : Repository.TryGet<IContent>(media.ContentLink, out var savedOver) ? savedOver : null;
            Operations.UploadOperation.RequireServedSafely(Call, media, NewFile, unchanged);
        }
        var asked = PropertyValues.Snapshot(writable);
        var diff = PropertyValues.Diff(before, asked);
        // What is reported: an editor never sees a hidden property's value, also not in a diff.
        var changes = Call.Properties.Shown(writable, diff);
        Project?.Check(writable);
        // The new shared blocks for its areas: checked with it, so both are valid or nothing is saved.
        var shared = Writer.Shared is { Count: > 0 } pending ? pending : null;
        var sharedIssues = shared is null ? [] : shared.Prepare(writable, Project).Concat(shared.PreviewNotes(action)).ToList();
        RequireBlocksPublishedForReview(writable, action, ContentReference.IsNullOrEmpty(writable.ContentLink) ? $"The new content '{writable.Name}'" : $"{writable.ContentLink.ID} ('{writable.Name}')");
        List<ValidationIssue> issues;
        using (shared?.AsInline(writable))
        {
            // For an editor, also the types the edit UI lets a property hold where an editor descriptor sets them, which the
            // CMS's own validation doesn't know.
            issues = (precheck ?? []).Concat(Writer.Warnings).Concat(sharedIssues).Concat(ValidationErrors.Validate(Call, writable, action))
                .Concat(Call.Properties.Checks ? EditUiTypeRules.Check(Call, writable, before) : []).ToList();
        }
        var valid = !ValidationErrors.HasErrors(issues);
        var published = Kind(action) == SaveAction.Publish;
        if (Project is null && !published && ProjectSave.NotInProject(Call, writable, intoVersion) is { } notInProject)
        {
            issues.Add(notInProject);
        }

        if (dryRun)
        {
            return new WriteResult
            {
                Content = shown,
                DryRun = true,
                Valid = valid,
                ScheduledFor = Kind(action) == SaveAction.Schedule && writable is IVersionable { StartPublish: { } scheduled } ? scheduled.ToUniversalTime() : null,
                BaseVersion = baseVersion,
                // A block it would make shows as what it will be, not by the GUID its item carries until it is saved.
                Changes = shared?.ShowPlanned(changes) ?? changes,
                Validation = issues.Count > 0 ? issues : null,
                IntoVersion = intoVersion is not null && (diff.Count > 0 || saveUnchanged),
                // The version the save goes into, or (nothing to save) the version the write is based on, which goes in as it is.
                Project = Project?.Planned(writable, review: Kind(action) == SaveAction.RequestApproval,
                    intoVersion is not null && (diff.Count > 0 || saveUnchanged) ? intoVersion
                    : diff.Count == 0 && !saveUnchanged && writable.ContentLink.WorkID > 0 ? writable.ContentLink.WorkID : null),
                SharedBlocks = DryRunSharedBlocks(shared, writable, action, issues),
            };
        }
        if (!valid)
        {
            throw AgentException.Invalid(issues);
        }
        // The gates above ran before anything changes (beforeSave may restore content) and outside the try below, whose
        // filter could take a refusal for a site failure after a save.
        ThrowIfAborted();
        beforeSave?.Invoke();
        // The blocks first: the content's areas then show them by their refs.
        var made = shared?.Save(writable, Project, action, issues) ?? [];
        // Blocks of its folder an add found already there (--if-missing), listed so a plan run again reaches them.
        made.AddRange(Writer.Shared?.Found(writable).Where(f => !made.Any(m => SameBlock(m, f))) ?? []);
        if (made.Any(m => m.Action != SharedBlockActions.Existing))
        {
            asked = PropertyValues.Snapshot(writable);
            diff = PropertyValues.Diff(before, asked);
            changes = Call.Properties.Shown(writable, diff);
        }
        if (diff.Count == 0 && !saveUnchanged)
        {
            if (Project is not null && !ContentReference.IsNullOrEmpty(writable.ContentLink) && writable.ContentLink.WorkID > 0)
            {
                // Nothing to save, but the version the write is based on may still go in the project (a plan run again).
                // With the decided action: a review request isn't marked ready (it is unreachable today, as a request with an
                // action saves even unchanged, but the item must never be checked in past its reviewers).
                var (project, warning) = Project.AddUnchanged(Repository.Get<IContent>(writable.ContentLink), action);
                var unchanged = Repository.Get<IContent>(writable.ContentLink);
                return new WriteResult
                {
                    Content = ContentSummaries.Describe(unchanged, Types),
                    BaseVersion = baseVersion,
                    Validation = warning is null ? (issues.Count > 0 ? issues : null) : [.. issues, warning],
                    SiteChanges = SiteChanges.Report(Call, Types, Call.TakeSiteEvents(), unchanged, null, published: false),
                    Project = project,
                    SharedBlocks = made.Count > 0 ? made : null,
                };
            }
            if (made.Any(m => m.Action != SharedBlockActions.Existing) && writable.ContentLink is { } based && !ContentReference.IsNullOrEmpty(based)
                && PublishAlong(Repository.Get<IContent>(based), action, OthersDraftsConfirmed, dryRun: false, issues) is { Count: > 0 } along)
            {
                // Nothing of the content changed, but a publish of it was asked for: its blocks' new versions go live with it.
                made = SharedAreaBlocks.Merge(made, along);
            }
            return new WriteResult { Content = shown, BaseVersion = baseVersion, Validation = issues.Count > 0 ? issues : null, SharedBlocks = made.Count > 0 ? made : null };
        }

        var isNew = ContentReference.IsNullOrEmpty(writable.ContentLink);
        var askedParent = isNew ? writable.ParentLink : null;
        var newestBefore = isNew || writable is not IVersionable ? 0 : NewestVersion(writable.ContentLink);
        var intoBefore = intoVersion is { } into ? Versions.Load(new ContentReference(writable.ContentLink.ID, into)) : null;
        ContentReference saved;
        string? siteError = null;
        try
        {
            try
            {
                saved = Call.Save(writable, action);
            }
            catch (System.ComponentModel.DataAnnotations.ValidationException ex) when (ValidationErrors.MasterNotPublished(writable, action, Repository))
            {
                throw ValidationErrors.MasterFirst(ex, Call);
            }
            catch (Exception ex) when (ex is not AgentException && intoBefore is not null && SaveInto.NewVersionRequired(action, ex) && SavedInto(intoBefore, before) is null)
            {
                // A site handler wants a new version on every save, which a save into the draft can't give.
                throw SaveInto.NewVersionRequiredError(intoBefore.ContentLink.WorkID);
            }
            catch (Exception ex) when (ex is not AgentException
                && (intoBefore is null ? SavedAnyway(writable, isNew, newestBefore) : SavedInto(intoBefore, before)) is { } stored)
            {
                // The CMS raises its post-save events inside Save: a site handler that throws (a search indexer, say) fails
                // the call after the content was saved. The save stands, so report it, or a plan would lose what it made.
                Call.SiteFailedAfterSave(stored, ex);
                saved = stored;
                siteError = $"{ex.Message} ({ex.GetType().FullName})";
            }
        }
        catch (Exception ex) when (made.Count > 0)
        {
            // The blocks were saved and stay: the caller gets their refs to retry with.
            throw SharedAreaBlocks.WithMadeBlocks(ex, made, null);
        }
        var result = Repository.Get<IContent>(saved);
        var overridden = Overridden(diff, writable, result);
        if (overridden.Count > 0)
        {
            // Report what was stored, not what was asked for.
            changes = Call.Properties.Shown(writable, Stored(diff, overridden));
        }
        var events = Call.TakeSiteEvents().ToList();
        if (!published && writable is IVersionable && saved.WorkID > 0 && Compat.CmsApi.Variation(writable) is null)
        {
            // ForceNewVersion leaves the old primary draft in place, and edit mode would keep opening that one. (Not for a
            // content variation's version: the primary draft is the content's own.)
            Versions.SetCommonDraft(saved);
        }
        ProjectSaveResult? inProject = null;
        if (Project is not null)
        {
            // Marking it ready is a save of its own, whose events the site's code may act on too.
            inProject = Project.Add(result, action);
            events.AddRange(Call.TakeSiteEvents());
            if (inProject.Ready)
            {
                result = Repository.Get<IContent>(saved);
            }
        }
        var siteChanges = SiteChanges.Report(Call, Types, events, result, askedParent, published,
            Call.Properties.Shown(result, overridden).Select(c => new SiteChangedProperty(c.Property, c.Before, c.After)).ToList());
        if ((published || Kind(action) == SaveAction.Schedule) && PublishAlong(result, action, OthersDraftsConfirmed, dryRun: false, issues) is { Count: > 0 } publishedAlong)
        {
            made = SharedAreaBlocks.Merge(made, publishedAlong);
        }
        return new WriteResult
        {
            Content = ContentSummaries.Describe(result, Types),
            Saved = true,
            // A handler of the publishing events may have failed before the version went live.
            Published = published && (siteError is null || result is not IVersionable { Status: not VersionStatus.Published }),
            ApprovalRequested = Kind(action) == SaveAction.RequestApproval,
            ScheduledFor = Kind(action) == SaveAction.Schedule && result is IVersionable { StartPublish: { } at } ? at.ToUniversalTime() : null,
            BaseVersion = baseVersion,
            Changes = changes,
            Validation = issues.Count > 0 ? issues : null,
            SiteError = siteError,
            SiteChanges = siteChanges,
            IntoVersion = intoVersion is { } target && saved.WorkID == target,
            Project = inProject,
            SharedBlocks = made.Count > 0 ? made : null,
        };
    }

    /// <summary>A dry run's <see cref="WriteResult.SharedBlocks"/>: the blocks it would make or change, and the drafts a publish would publish along.</summary>
    private List<SharedBlockResult>? DryRunSharedBlocks(SharedAreaBlocks? shared, IContent writable, SaveAction action, List<ValidationIssue> issues)
    {
        var planned = shared?.Planned(action) ?? [];
        planned.AddRange(Writer.Shared?.Found(writable) ?? []);
        if (!ContentReference.IsNullOrEmpty(writable.ContentLink) && PublishAlong(writable, action, OthersDraftsConfirmed, dryRun: true, issues) is { Count: > 0 } along)
        {
            planned.AddRange(along.Where(a => !planned.Any(p => p.Ref is not null && a.Ref?.Split('_')[0] == p.Ref.Split('_')[0])));
        }
        return planned.Count > 0 ? planned : null;
    }

    private static bool SameBlock(SharedBlockResult a, SharedBlockResult b) =>
        a.Ref is not null && b.Ref is not null && a.Ref.Split('_')[0] == b.Ref.Split('_')[0];

    /// <summary>Properties the CMS fills in on save when they are empty.</summary>
    private static readonly HashSet<string> FilledByCms = new(StringComparer.OrdinalIgnoreCase) { "PageURLSegment", PropertyWriter.StartPublishKey };

    /// <summary>
    /// The properties the write changed that the site's own code changed again while it was saved (a handler of the CMS's
    /// saving events, on the content being saved): <c>Before</c> what the write asked for, <c>After</c> what was stored.
    /// </summary>
    /// <remarks>
    /// Told by the content the save was given, which the CMS hands to those handlers: compared with the stored version,
    /// rich text and links would differ only in how the CMS stores them. Only properties the write changed: the CMS fills
    /// in others itself (a new page's URL segment, the publish date), and those it fills in when the write left them empty.
    /// </remarks>
    private static List<PropertyChange> Overridden(IReadOnlyList<PropertyChange> diff, IContent saving, IContent stored)
    {
        if (diff.Count == 0)
        {
            return [];
        }
        var now = PropertyValues.Snapshot(saving);
        Dictionary<string, JsonElement?>? values = null;
        var overridden = new List<PropertyChange>();
        // A composition's nodes get their keys as it is saved; an empty URL segment or publish date is filled in by the CMS
        // (a new language branch's, a publish's).
        foreach (var change in diff.Where(c => !c.Property.Equals(PropertyValues.CompositionKey, StringComparison.OrdinalIgnoreCase)
            && !(c.After is null && FilledByCms.Contains(c.Property))))
        {
            if (now.GetValueOrDefault(change.Property)?.GetRawText() != change.After?.GetRawText())
            {
                values ??= PropertyValues.Snapshot(stored);
                overridden.Add(new PropertyChange(change.Property, change.After, values.GetValueOrDefault(change.Property)));
            }
        }
        return overridden;
    }

    /// <summary><paramref name="diff"/> with the stored values of <paramref name="overridden"/>, without those the site set back to what they were.</summary>
    private static List<PropertyChange> Stored(IReadOnlyList<PropertyChange> diff, IReadOnlyList<PropertyChange> overridden)
    {
        var stored = overridden.ToDictionary(c => c.Property, c => c.After, StringComparer.OrdinalIgnoreCase);
        return diff
            .Select(c => stored.TryGetValue(c.Property, out var value) ? c with { After = value } : c)
            .Where(c => c.Before?.GetRawText() != c.After?.GetRawText())
            .ToList();
    }

    /// <summary>The highest version id of the content; 0 when it has none.</summary>
    private int NewestVersion(ContentReference link) =>
        Versions.List(link.ToReferenceWithoutVersion()).Select(v => v.ContentLink.WorkID).DefaultIfEmpty(0).Max();

    /// <summary>
    /// After a failed save into <paramref name="version"/>: that version, when the save changed it all the same (its
    /// values, status or saved time); null when it is as it was. No new version to look for.
    /// </summary>
    /// <param name="values">Its values before the save (<see cref="PropertyValues.Snapshot"/>).</param>
    private ContentReference? SavedInto(ContentVersion version, IReadOnlyDictionary<string, JsonElement?> values)
    {
        var now = Versions.Load(version.ContentLink);
        var changed = now is not null && (now.Status != version.Status || now.Saved != version.Saved
            || PropertyValues.Diff(values, PropertyValues.Snapshot(Repository.Get<IContent>(version.ContentLink))).Count > 0);
        return changed ? version.ContentLink : null;
    }

    /// <summary>
    /// After a failed save: the version it saved all the same, found by the new content's GUID, or as a version of
    /// existing content newer than <paramref name="newestBefore"/>. Null when nothing was saved, or it can't be told
    /// (content without versions).
    /// </summary>
    /// <remarks>
    /// New content found by its GUID counts only with a version stored: a save that failed inside the CMS's own
    /// transaction (a value the database can't take) may still be found by its GUID for a moment, with nothing of it kept.
    /// </remarks>
    private ContentReference? SavedAnyway(IContent writable, bool isNew, int newestBefore)
    {
        if (isNew)
        {
            if (Saved(writable) is not { } stored || writable is not IVersionable)
            {
                return Saved(writable)?.ContentLink;
            }
            try
            {
                return Versions.List(stored.ContentLink.ToReferenceWithoutVersion()).OrderByDescending(v => v.ContentLink.WorkID).FirstOrDefault()?.ContentLink;
            }
            catch (ContentNotFoundException)
            {
                return null;
            }
        }
        if (newestBefore == 0)
        {
            return null;
        }
        var newest = Versions.List(writable.ContentLink.ToReferenceWithoutVersion()).OrderByDescending(v => v.ContentLink.WorkID).FirstOrDefault();
        return newest is not null && newest.ContentLink.WorkID > newestBefore ? newest.ContentLink : null;
    }
}
