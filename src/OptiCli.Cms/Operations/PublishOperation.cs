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
        if (version is IVersionable { Status: VersionStatus.Published } && !expired)
        {
            throw AgentException.Conflict($"Version {version.ContentLink} is already the published version.");
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
        if (action == SaveAction.RequestApproval && version is IVersionable { Status: VersionStatus.AwaitingApproval })
        {
            Approvals.RequireNotInReview([.. branch.Where(v => v.ContentLink.WorkID == version.ContentLink.WorkID)], what, versionLanguage?.Name);
        }
        // The approval would publish it alone: not while it shows blocks of its own folder that were never published.
        flow.RequireBlocksPublishedForReview(version, action, what, publishing: true);
        // The blocks of its own folder the caller names: taken along although live, or kept back although never published.
        var choice = flow.Choice(body.WithBlocks, body.WithoutBlocks, WithName(call), WithoutName(call), call.ForCaller<Func<string, string>>(
            block => $"Name it to publish that draft with the content (opticli publish {link.ID} --with-block {block}), or publish it on its own (opticli publish {block}).",
            block => $"Pass blocks: [\"{block}\"] to publish that draft with the content, or publish the block on its own."));
        if (choice.Names)
        {
            flow.Writer.Shared!.RequireChosen([version], choice, WithName(call), WithoutName(call));
        }
        var othersConfirmed = body.IncludeDraft || (named is not null && call.NamedVersionConfirmsDraft);
        var pending = publishing || scheduling
            ? PendingDrafts.Require(flow.Locator.PendingDraft(branch, version), othersConfirmed, body.DryRun, what, versionLanguage?.Name)
            : action == SaveAction.RequestApproval && call.ReportsDraftInReview
                ? flow.Locator.PendingDraft(branch, version)
                : null;
        var previouslyPublished = ContentLocator.PublishedVersion(branch);

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
                ? flow.PublishAlong(version, action, othersConfirmed, dryRun: true, planned, at: body.PublishAt?.ToLocalTime(), choice: choice)
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
            && flow.Repository.Get<IContent>(version.ContentLink) is IVersionable { Status: var status }
            && status == (publishing ? VersionStatus.Published : scheduling ? VersionStatus.DelayedPublish : VersionStatus.AwaitingApproval))
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
            ? flow.PublishAlong(result, action, othersConfirmed, dryRun: false, warnings, choice: choice)
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

    /// <summary>How the caller names the blocks to take along: the CLI's flag, or the MCP tool's parameter.</summary>
    internal static string WithName(CmsCall call) => call.ForCaller("--with-block", "blocks");

    /// <summary>How the caller names the blocks to keep back.</summary>
    internal static string WithoutName(CmsCall call) => call.ForCaller("--without-block", "skipBlocks");
}
