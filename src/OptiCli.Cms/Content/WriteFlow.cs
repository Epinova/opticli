using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Validation;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// What create, draft and languages share: apply the request to a writable instance, diff it against
/// its starting point, validate, then either report (dry run) or save.
/// </summary>
internal sealed class WriteFlow
{
    public WriteFlow(CmsCall call)
    {
        Call = call;
        Types = call.Service<IContentTypeRepository>();
        Versions = call.Service<IContentVersionRepository>();
        Locator = new ContentLocator(call);
        Writer = new PropertyWriter(Locator, new BlockFactory(
            call.Service<EPiServer.Construction.IContentDataFactory<BlockData>>(), call.Service<EPiServer.Construction.IContentDataBuilder>(), Types),
            call.Service<CategoryRepository>(), call.Service<IFrameRepository>(), call.Service<EPiServer.Web.DisplayOptions>());
        Areas = new AreaEditor(Locator, Writer);
        Validation = call.Service<IValidationService>();
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

    public IValidationService Validation { get; }

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
    public WriteResult Save(
        IContent writable,
        IReadOnlyDictionary<string, JsonElement?> before,
        SaveAction action,
        bool dryRun,
        ContentSummary? shown,
        int? baseVersion,
        bool saveUnchanged,
        IEnumerable<ValidationIssue>? precheck = null,
        Action? beforeSave = null)
    {
        var changes = PropertyValues.Diff(before, PropertyValues.Snapshot(writable));
        var issues = (precheck ?? []).Concat(ValidationErrors.Validate(Validation, writable, action)).ToList();
        var valid = !ValidationErrors.HasErrors(issues);
        var published = Kind(action) == SaveAction.Publish;

        if (dryRun)
        {
            return new WriteResult
            {
                Content = shown,
                DryRun = true,
                Valid = valid,
                ScheduledFor = Kind(action) == SaveAction.Schedule && writable is IVersionable { StartPublish: { } scheduled } ? scheduled.ToUniversalTime() : null,
                BaseVersion = baseVersion,
                Changes = changes,
                Validation = issues.Count > 0 ? issues : null,
            };
        }
        if (!valid)
        {
            throw AgentException.Invalid(issues);
        }
        ThrowIfAborted();
        beforeSave?.Invoke();
        if (changes.Count == 0 && !saveUnchanged)
        {
            return new WriteResult { Content = shown, BaseVersion = baseVersion, Validation = issues.Count > 0 ? issues : null };
        }

        var isNew = ContentReference.IsNullOrEmpty(writable.ContentLink);
        var newestBefore = isNew || writable is not IVersionable ? 0 : NewestVersion(writable.ContentLink);
        ContentReference saved;
        string? siteError = null;
        try
        {
            saved = Call.Save(writable, action);
        }
        catch (System.ComponentModel.DataAnnotations.ValidationException ex) when (ValidationErrors.MasterNotPublished(writable, action, Repository))
        {
            throw ValidationErrors.MasterFirst(ex);
        }
        catch (Exception ex) when (ex is not AgentException && SavedAnyway(writable, isNew, newestBefore) is { } stored)
        {
            // The CMS raises its post-save events inside Save: a site handler that throws (a search indexer, say) fails
            // the call after the content was saved. The save stands, so report it, or a plan would lose what it made.
            Console.Error.WriteLine($"[opticli] the site failed after saving {stored}: {ex}");
            saved = stored;
            siteError = $"{ex.Message} ({ex.GetType().FullName})";
        }
        var result = Repository.Get<IContent>(saved);
        if (!published && writable is IVersionable)
        {
            // ForceNewVersion leaves the old primary draft in place, and edit mode would keep opening that one.
            Versions.SetCommonDraft(saved);
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
        };
    }

    /// <summary>The highest version id of the content; 0 when it has none.</summary>
    private int NewestVersion(ContentReference link) =>
        Versions.List(link.ToReferenceWithoutVersion()).Select(v => v.ContentLink.WorkID).DefaultIfEmpty(0).Max();

    /// <summary>
    /// After a failed save: the version it saved all the same, found by the new content's GUID, or as a version of
    /// existing content newer than <paramref name="newestBefore"/>. Null when nothing was saved, or it can't be told
    /// (content without versions).
    /// </summary>
    private ContentReference? SavedAnyway(IContent writable, bool isNew, int newestBefore)
    {
        if (isNew)
        {
            return Saved(writable)?.ContentLink;
        }
        if (newestBefore == 0)
        {
            return null;
        }
        var newest = Versions.List(writable.ContentLink.ToReferenceWithoutVersion()).OrderByDescending(v => v.ContentLink.WorkID).FirstOrDefault();
        return newest is not null && newest.ContentLink.WorkID > newestBefore ? newest.ContentLink : null;
    }
}
