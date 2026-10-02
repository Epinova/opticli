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
    public static WriteResult Run(CmsCall call, string reference, PublishRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.Resolve(reference);
        if (body.Version is { } explicitVersion && link.WorkID > 0 && explicitVersion != link.WorkID)
        {
            throw AgentException.Usage($"The ref names version {link.WorkID} but the body says {explicitVersion}.");
        }

        // A named version is the caller's choice of what goes live; the latest one may hold someone else's draft.
        var named = body.Version ?? (link.WorkID > 0 ? link.WorkID : (int?)null);
        IReadOnlyList<ContentVersion>? branch = null;
        var versionId = named ?? 0;
        if (named is null)
        {
            var language = flow.Locator.ContentLanguage(flow.Locator.LoadAnyLanguage(link), body.Lang);
            branch = flow.Locator.Versions(link, language);
            versionId = ContentLocator.Latest(branch, link, language).ContentLink.WorkID;
        }

        var version = flow.Repository.Get<IContent>(new ContentReference(link.ID, versionId));
        if (version.ContentLink.ID != link.ID)
        {
            throw AgentException.NotFound($"Content {link.ID} has no version {versionId}.");
        }
        // After the id check: a version id of other content loads that content, whose existence mustn't leak.
        call.RequireRead(version);
        // The published version taken offline (unpublish) is published again as a new version, without its stop date.
        var expired = version is IVersionable { Status: VersionStatus.Published, StopPublish: { } stop } && stop <= DateTime.Now;
        if (version is IVersionable { Status: VersionStatus.Published } && !expired)
        {
            throw AgentException.Conflict($"Version {version.ContentLink} is already the published version.");
        }

        var versionLanguage = version is ILocalizable { Language: { } own } ? own : null;
        branch ??= flow.Locator.Versions(link, versionLanguage);
        var what = $"{link.ID} ('{version.Name}')";
        var action = WriteFlow.Publishing(call, link, publish: true, body.RequestApproval, body.PublishAt, what) ?? SaveAction.Publish;
        var publishing = action == SaveAction.Publish;
        var scheduling = action == SaveAction.Schedule;
        if (action == SaveAction.RequestApproval && version is IVersionable { Status: VersionStatus.AwaitingApproval })
        {
            Approvals.RequireNotInReview([.. branch.Where(v => v.ContentLink.WorkID == version.ContentLink.WorkID)], what, versionLanguage?.Name);
        }
        var pending = publishing || scheduling
            ? PendingDrafts.Require(flow.Locator.PendingDraft(branch, version), body.IncludeDraft || named is not null, dryRun: false, what, versionLanguage?.Name)
            : null;
        var previouslyPublished = ContentLocator.PublishedVersion(branch);

        var writable = (IContent)((IReadOnly)version).CreateWritableClone();
        WriteFlow.ScheduleAt(writable, action, body.PublishAt);
        var cleared = WriteFlow.ClearExpiredStopPublish(writable, action, null);
        if (expired)
        {
            action |= SaveAction.ForceNewVersion;
        }
        var issues = ValidationErrors.Validate(flow.Validation, writable, action);
        if (cleared is not null)
        {
            issues = [.. issues, cleared];
        }
        if (ValidationErrors.HasErrors(issues))
        {
            throw AgentException.Invalid(issues);
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
            throw ValidationErrors.MasterFirst(ex);
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
        return new WriteResult
        {
            Content = ContentSummaries.Describe(flow.Repository.Get<IContent>(saved), flow.Types),
            SiteError = siteError,
            Saved = true,
            Published = publishing,
            ApprovalRequested = action == SaveAction.RequestApproval,
            ScheduledFor = scheduling ? body.PublishAt!.Value.ToUniversalTime() : null,
            BaseVersion = versionId,
            Validation = issues.Count > 0 ? issues : null,
            PendingDraft = pending,
            PreviouslyPublished = publishing ? previouslyPublished : null,
        };
    }
}
