using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Data.Entity;
using EPiServer.DataAccess;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Publishes a version that exists (the agent's <c>POST /v1/content/{ref}/publish</c>): the latest, or the one named;
/// also scheduled, or sent for review where an approval sequence applies.
/// </summary>
internal static class PublishOperation
{
    public static WriteResult Run(CmsCall call, string reference, PublishRequest body) => Run(call, reference, body, new WriteFlow(call));

    /// <param name="flow">The write's flow: tests choose how the site makes new area blocks.</param>
    internal static WriteResult Run(CmsCall call, string reference, PublishRequest body, WriteFlow flow)
    {
        var link = flow.Locator.Resolve(reference);
        if (body.Version is { } explicitVersion && link.WorkID > 0 && explicitVersion != link.WorkID)
        {
            throw AgentException.Usage($"The ref names version {link.WorkID} but the body says {explicitVersion}.");
        }

        // A named version is the developer's choice of what goes live (CmsCall.NamedVersionConfirmsDraft); the latest one
        // may hold someone else's draft.
        var named = body.Version ?? (link.WorkID > 0 ? link.WorkID : (int?)null);
        IReadOnlyList<ContentVersion>? branch = null;
        var versionId = named ?? 0;
        if (named is null)
        {
            var language = flow.Locator.ContentLanguage(flow.Locator.LoadAnyLanguage(link), body.Lang);
            branch = flow.Locator.Versions(link, language);
            versionId = ContentLocator.Latest(branch, link, language).ContentLink.WorkID;
        }

        var version = flow.Locator.Version(link, versionId);
        // After the id check: a version id of other content loads that content, whose existence mustn't leak.
        call.RequireRead(version);
        call.RequireLanguageAccess(version);
        // The published version taken offline (unpublish) is published again as a new version, without its stop date.
        var expired = version is IVersionable { Status: VersionStatus.Published, StopPublish: { } stop } && stop <= DateTime.Now;
        // Published already: where the site makes shared area blocks, the drafts of its blocks that a publish takes along may
        // still go live on their own (a plan's publish after steps that changed only blocks, or blocks named); else, and
        // when there are none, there is nothing to publish.
        var publishedAlready = version is IVersionable { Status: VersionStatus.Published } && !expired;
        if (publishedAlready && flow.Writer.Shared is null)
        {
            throw AgentException.Conflict(AlreadyPublished(version));
        }

        var versionLanguage = version is ILocalizable { Language: { } own } ? own : null;
        // A named version of a content variation (CMS 13) is published among that variation's versions.
        branch ??= flow.Locator.Versions(link, versionLanguage, Compat.CmsApi.Variation(version));
        var what = $"{link.ID} ('{version.Name}')";
        // An editor's requestApproval only asks for review, never publishes (see CmsCall.RequestApprovalMayPublish).
        var publish = !body.RequestApproval || call.RequestApprovalMayPublish;
        var action = WriteFlow.Publishing(call, link, publish, body.RequestApproval, body.PublishAt, what) ?? SaveAction.Publish;
        var publishing = action == SaveAction.Publish;
        var scheduling = action == SaveAction.Schedule;
        // The blocks of its own folder the caller names: taken along although live, or kept back although never published;
        // and for a plan's publish step, the block drafts earlier steps of the run saved, which go along unnamed.
        var choice = flow.Choice(body.WithBlocks, body.WithoutBlocks, WithName(call), WithoutName(call), call.ForCaller<Func<string, string>>(
            block => $"Name it to publish that draft with the content (opticli publish {link.ID} --with-block {block}), or publish it on its own (opticli publish {block}).",
            block => $"Pass blocks: [\"{block}\"] to publish that draft with the content, or publish the block on its own.")) with
        {
            WrittenVersions = flow.Writer.Shared is null ? new HashSet<(int, int)>() : SharedAreaBlocks.AlongChoice.Versions(body.WrittenBlocks),
            KeepOthers = body.KeepOthersBlockDrafts,
        };
        // A review publishes no blocks along: naming them there is refused, not ignored.
        flow.RequireNoneNamedForReview(choice, action, WithName(call), WithoutName(call));
        if (publishedAlready && !(publishing || scheduling))
        {
            throw AgentException.Conflict(AlreadyPublished(version));
        }
        if (action == SaveAction.RequestApproval && version is IVersionable { Status: VersionStatus.AwaitingApproval })
        {
            Approvals.RequireNotInReview([.. branch.Where(v => v.ContentLink.WorkID == version.ContentLink.WorkID)], what, versionLanguage?.Name);
        }
        // The approval would publish it alone: not while it shows blocks of its own folder that were never published (or
        // whose drafts earlier steps of a plan run saved).
        flow.RequireBlocksPublishedForReview(version, action, what, publishing: true, choice.WrittenVersions);
        if (choice.Names)
        {
            flow.Writer.Shared!.RequireChosen([version], choice, WithName(call), WithoutName(call));
        }
        if (publishedAlready)
        {
            return BlocksOnly(call, flow, version, versionId, action, choice, body);
        }
        // Someone else's draft of the content goes live only confirmed; their drafts of its blocks only when named (choice).
        var othersConfirmed = body.IncludeDraft || (named is not null && call.NamedVersionConfirmsDraft);
        var pending = publishing || scheduling
            ? PendingDrafts.Require(flow.Locator.PendingDraft(branch, version), othersConfirmed, body.DryRun, what, versionLanguage?.Name)
            : action == SaveAction.RequestApproval && call.ReportsDraftInReview
                ? flow.Locator.PendingDraft(branch, version)
                : null;
        var previouslyPublished = ContentLocator.PublishedVersion(branch);
        // A named version older than the published one replaces it, and the changes published in it: someone else's need
        // confirming, as their drafts do (the developer's named version confirms them; the latest version is never older).
        var newer = publishing || scheduling ? PendingDrafts.NewerPublished(branch, version.ContentLink.WorkID) : null;
        if (newer is not null && !othersConfirmed && !body.DryRun && PendingDrafts.PublishedByOthers(newer, call.UserName))
        {
            throw PendingDrafts.ReplacesPublished(versionLanguage is null ? what : $"{what} in '{versionLanguage.Name}'", version.ContentLink, newer);
        }

        var writable = (IContent)((IReadOnly)version).CreateWritableClone();
        WriteFlow.ScheduleAt(writable, action, body.PublishAt);
        var cleared = WriteFlow.ClearExpiredStopPublish(writable, action, null);
        if (expired)
        {
            action |= SaveAction.ForceNewVersion;
        }
        var issues = ValidationErrors.Validate(call, writable, action);
        if (cleared is not null)
        {
            issues = [.. issues, cleared];
        }
        if (newer is not null)
        {
            var unconfirmed = body.DryRun && !othersConfirmed && PendingDrafts.PublishedByOthers(newer, call.UserName);
            issues = [.. issues, PendingDrafts.Replaced(version.ContentLink, newer,
                body.DryRun ? unconfirmed ? $"would replace (the publish needs {call.ForCaller("--include-draft", "includeDraft")} to confirm that)" : "would replace"
                : scheduling ? "will replace" : "replaced")];
        }
        if (!body.DryRun && ValidationErrors.HasErrors(issues))
        {
            throw AgentException.Invalid(issues);
        }

        // Outside the try below, whose filter could take the gate's refusal for a site failure after the save (a version
        // already scheduled looks saved).
        call.RequirePublishing(action, writable);
        if (!call.MayWriteScript && writable is MediaData media)
        {
            // A media version saved elsewhere: what it would put live under its name, as WriteFlow.Save checks a save.
            UploadOperation.RequireServedSafely(call, media, null,
                previouslyPublished is { } live && flow.Repository.TryGet<IContent>(new ContentReference(version.ContentLink.ID, live, version.ContentLink.ProviderName), out var published) ? published : null);
        }
        if (body.DryRun)
        {
            // Everything the publish checks, and what it would put live, without saving: the blocks of its folder that would
            // go along (and those it leaves behind, as warnings).
            var planned = issues.ToList();
            var plannedAlong = publishing || scheduling
                ? flow.PublishAlong(version, action, dryRun: true, planned, at: body.PublishAt?.ToLocalTime(), choice: choice)
                : null;
            return new WriteResult
            {
                Content = ContentSummaries.Describe(version, flow.Types),
                DryRun = true,
                Valid = !ValidationErrors.HasErrors(planned),
                ScheduledFor = scheduling ? body.PublishAt!.Value.ToUniversalTime() : null,
                BaseVersion = versionId,
                Validation = planned.Count > 0 ? planned : null,
                PendingDraft = pending,
                PreviouslyPublished = publishing ? previouslyPublished : null,
                SharedBlocks = plannedAlong is { Count: > 0 } ? plannedAlong : null,
            };
        }
        flow.ThrowIfAborted();
        ContentReference saved;
        string? siteError = null;
        // As stored before the save: a version that has the status the save gives it already (one scheduled before, being
        // scheduled again) went through the save only when its stamp changed.
        var stampBefore = flow.Versions.Load(version.ContentLink);
        try
        {
            saved = call.Save(writable, action);
        }
        catch (System.ComponentModel.DataAnnotations.ValidationException ex) when (ValidationErrors.MasterNotPublished(writable, action, flow.Repository))
        {
            throw ValidationErrors.MasterFirst(ex, call);
        }
        catch (Exception ex) when (ex is not AgentException
            && !expired
            && SavedAnyway(flow, version.ContentLink, stampBefore,
                publishing ? VersionStatus.Published : scheduling ? VersionStatus.DelayedPublish : VersionStatus.AwaitingApproval))
        {
            // A handler of the publishing events failed after the version went live (see WriteFlow.Save).
            call.SiteFailedAfterSave(version.ContentLink, ex);
            saved = version.ContentLink;
            siteError = $"{ex.Message} ({ex.GetType().FullName})";
        }
        var result = flow.Repository.Get<IContent>(saved);
        // The drafts of the blocks of its "For this page" folder that it shows, where the site's edit UI makes those: those
        // never published, and those the caller names (choice).
        var warnings = issues.ToList();
        var along = publishing || scheduling
            ? flow.PublishAlong(result, action, dryRun: false, warnings, choice: choice)
            : null;
        issues = warnings;
        return new WriteResult
        {
            Content = ContentSummaries.Describe(result, flow.Types),
            SiteError = siteError,
            SiteChanges = SiteChanges.Report(call, flow.Types, call.TakeSiteEvents(), result, null, publishing),
            Saved = true,
            Published = publishing,
            ApprovalRequested = action == SaveAction.RequestApproval,
            ScheduledFor = scheduling ? body.PublishAt!.Value.ToUniversalTime() : null,
            BaseVersion = versionId,
            Validation = issues.Count > 0 ? issues : null,
            PendingDraft = pending,
            PreviouslyPublished = publishing ? previouslyPublished : null,
            SharedBlocks = along is { Count: > 0 } ? along : null,
        };
    }

    /// <summary>
    /// After a failed save: whether the version went through it all the same (a handler of the publishing events failed
    /// after it), so it now has <paramref name="target"/>. A version that had that status before counts only when its
    /// status or saved time changed: rescheduling a scheduled version that failed before saving leaves it as it was.
    /// </summary>
    private static bool SavedAnyway(WriteFlow flow, ContentReference version, ContentVersion? before, VersionStatus target)
    {
        var now = flow.Versions.Load(version);
        return now is not null && now.Status == target
            && (before is null || before.Status != target || before.Saved != now.Saved);
    }

    private static string AlreadyPublished(IContent version) => $"Version {version.ContentLink} is already the published version.";

    /// <summary>
    /// A publish of a version that is published already, where the site makes shared area blocks: the content has nothing
    /// new, but the drafts of the blocks of its own "For this page" folder that the publish names, or that earlier steps of
    /// a plan run saved, go live on their own, as they would have with it. A plan's publish after steps that changed only
    /// blocks publishes those, as one write that publishes them does. A block never published goes along only when named
    /// too (decision 13 of the plan): it isn't new with this publish, so someone probably kept it back.
    /// </summary>
    /// <exception cref="AgentException">
    /// <c>conflict</c> when no block goes along either: there is nothing to publish. Its hint lists the blocks a publish
    /// takes along when named, and how to name them.
    /// </exception>
    private static WriteResult BlocksOnly(CmsCall call, WriteFlow flow, IContent version, int versionId, SaveAction action,
        SharedAreaBlocks.AlongChoice choice, PublishRequest body)
    {
        call.RequirePublishing(action, version);
        choice = choice with { NamedOnly = true };
        var scheduling = action == SaveAction.Schedule;
        var at = body.PublishAt?.ToLocalTime();
        var planned = new List<ValidationIssue>();
        var nameable = new List<SharedAreaBlocks.Nameable>();
        var keptOthers = new List<int>();
        var along = flow.PublishAlong(version, action, dryRun: true, planned, at: at, choice: choice, nameable: nameable, keptOthers: keptOthers) ?? [];
        if (along.Count == 0 && keptOthers.Count > 0)
        {
            // A sealed apply named blocks whose drafts someone else saved in production: they stay, and so does everything
            // else. The run goes on (nothing is published, the step is unchanged) rather than stop half-way in production.
            planned.Insert(0, new ValidationIssue(null,
                $"{AlreadyPublished(version)} Nothing {(body.DryRun ? "would be" : "was")} published: the blocks the publish names are drafts someone else saved, which a sealed apply leaves as they are.",
                "info"));
            return new WriteResult
            {
                Content = ContentSummaries.Describe(version, flow.Types),
                DryRun = body.DryRun,
                Valid = true,
                BaseVersion = versionId,
                Validation = planned,
            };
        }
        if (along.Count == 0)
        {
            // The blocks it could name, listed once; the warnings about any other block (a named one that can't go) as they are.
            var others = planned.Where(i => !nameable.Any(n => n.Path == i.Property)).Select(i => i.Message).ToList();
            var hint = string.Join(" ", (nameable.Count > 0 ? [flow.Writer.Shared!.NameableHint(version, nameable)] : Array.Empty<string>()).Concat(others));
            throw AgentException.Conflict(
                $"{AlreadyPublished(version)} No draft of a block of its \"For this page\" folder goes along either (with the content published already, only the blocks a publish names go along, or those the same plan run saved), so there is nothing to publish.",
                hint.Length > 0 ? hint : null);
        }
        var issues = body.DryRun ? planned : [];
        if (!body.DryRun)
        {
            flow.ThrowIfAborted();
            along = flow.PublishAlong(version, action, dryRun: false, issues, at: at, choice: choice) ?? [];
            // The blocks' saves are the publish's own, not the site's changes.
            call.TakeSiteEvents();
        }
        issues.Insert(0, new ValidationIssue(null,
            $"{AlreadyPublished(version)} Only the drafts of the blocks of its \"For this page\" folder in sharedBlocks {(body.DryRun ? "would be" : "were")} {(scheduling ? "scheduled" : "published")}; the content itself stays as it is.",
            "info"));
        return new WriteResult
        {
            Content = ContentSummaries.Describe(version, flow.Types),
            DryRun = body.DryRun,
            Valid = true,
            ScheduledFor = scheduling ? body.PublishAt!.Value.ToUniversalTime() : null,
            BaseVersion = versionId,
            Validation = issues,
            SharedBlocks = along.Count > 0 ? along : null,
        };
    }

    /// <summary>How the caller names the blocks to take along: the CLI's flag, or the MCP tool's parameter.</summary>
    internal static string WithName(CmsCall call) => call.ForCaller("--with-block", "blocks");

    /// <summary>How the caller names the blocks to keep back.</summary>
    internal static string WithoutName(CmsCall call) => call.ForCaller("--without-block", "skipBlocks");
}
