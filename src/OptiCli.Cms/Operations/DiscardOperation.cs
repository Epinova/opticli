using EPiServer.Core;
using EPiServer.DataAccess;
using EPiServer.Security;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Deletes one version that was never published (the agent's <c>POST /v1/content/{ref}/discard</c>), the latest by
/// default; never the published version, the history, or a branch's only version.
/// </summary>
/// <remarks>
/// A version is deleted for good, so for an editor discarding what someone else saved takes what deleting takes (the
/// caller's deleting gate) besides <c>includeDraft</c>, which only confirms it; and discarding a version scheduled for
/// publishing, which cancels that publish, takes what publishing takes. Both are checked for a dry run too.
/// </remarks>
internal static class DiscardOperation
{
    public static WriteResult Run(CmsCall call, string reference, DiscardRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.Resolve(reference);
        if (body.Version is { } explicitVersion && link.WorkID > 0 && explicitVersion != link.WorkID)
        {
            throw AgentException.Usage($"The ref names version {link.WorkID} but the body says {explicitVersion}.");
        }
        var content = flow.Locator.LoadAnyLanguage(link);
        if (content is not IVersionable)
        {
            throw AgentException.Usage($"Content {link.ID} ({flow.Types.Load(content.ContentTypeID)?.Name}) has no versions to discard.");
        }

        var named = body.Version ?? (link.WorkID > 0 ? link.WorkID : (int?)null);
        var language = named is null ? flow.Locator.ContentLanguage(content, body.Lang) : null;
        var version = flow.Locator.Version(link, named ?? ContentLocator.Latest(flow.Locator.Versions(link, language), link, language).ContentLink.WorkID);
        // The CMS's version repository requires Delete on the content to delete a version, also a draft the editor saved
        // themselves; checked first, so the refusal names the content and nothing is compared or loaded for nothing.
        call.RequireAccess(version, AccessLevel.Delete);
        var versionLanguage = version is ILocalizable { Language: { } own } ? own : null;
        // CMS 13: a content variation's version is discarded among that variation's versions; the content keeps its own.
        var variation = Compat.CmsApi.Variation(version);
        var branch = flow.Locator.Versions(link, versionLanguage, variation);
        var contentVersions = variation is null ? branch : flow.Locator.Versions(link, versionLanguage);
        var stamp = branch.First(v => v.ContentLink.WorkID == version.ContentLink.WorkID);
        var what = $"Version {version.ContentLink} of {link.ID} ('{version.Name}')";

        switch (stamp.Status)
        {
            case VersionStatus.Published:
                throw AgentException.Refused($"{what} is the published version; discarding it would leave the content without one.",
                    "Take it offline with unpublish instead, or publish another version first.");
            case VersionStatus.PreviouslyPublished:
                throw AgentException.Refused($"{what} was published before; opticli only discards versions that were never published, which keeps the history.",
                    "Leave it; it isn't live.");
            case VersionStatus.AwaitingApproval:
                Approvals.RequireNotInReview([stamp], what, versionLanguage?.Name);
                break;
        }
        if (branch.Count == 1 && variation is null)
        {
            throw AgentException.Refused($"{what} is the only version{(versionLanguage is null ? "" : $" of the '{versionLanguage.Name}' branch")}; discarding it would delete the content.",
                "Move the content to the recycle bin instead (delete).");
        }
        call.RequireLanguageAccess(version);
        if (stamp.Status == VersionStatus.DelayedPublish)
        {
            // Discarding it cancels the scheduled publish: a change to what visitors will see.
            call.RequirePublishing(SaveAction.Schedule);
        }

        // What is lost: the version's values compared with what stays, the published version or else the one before it.
        // (A variation's only version leaves the content's own: its published version, else its latest.)
        var kept = ContentLocator.PublishedVersion(branch)
            ?? branch.Where(v => v.ContentLink.WorkID != stamp.ContentLink.WorkID).Select(v => (int?)v.ContentLink.WorkID).FirstOrDefault()
            ?? ContentLocator.PublishedVersion(contentVersions)
            ?? contentVersions.First().ContentLink.WorkID;
        var changes = call.Properties.Shown(version,
            PropertyValues.Diff(PropertyValues.Snapshot(flow.Repository.Get<IContent>(new ContentReference(link.ID, kept))), PropertyValues.Snapshot(version)));
        var pending = PendingDrafts.SavedBy(stamp.SavedBy, call.UserName)
            ? null
            : new PendingDraft(stamp.ContentLink.ToString(), stamp.SavedBy, stamp.Saved.ToUniversalTime(), changes);
        if (pending is not null)
        {
            // Someone else's work, deleted for good: includeDraft only confirms it.
            call.RequireDeleting();
        }
        if (pending is not null && !body.IncludeDraft && !body.DryRun)
        {
            throw new AgentException(AgentErrorCodes.Conflict,
                $"{what} was saved by {(string.IsNullOrWhiteSpace(stamp.SavedBy) ? "nobody signed in (a scheduled job or import)" : stamp.SavedBy)}, not {call.UserName}; discarding it deletes their changes for good.",
                "Ask whether those changes should be thrown away; if so, retry with includeDraft.")
            {
                PendingDraft = pending,
            };
        }

        var shown = ContentSummaries.Describe(version, flow.Types);
        if (!body.DryRun)
        {
            flow.ThrowIfAborted();
            call.DeleteVersion(version);
        }
        return new WriteResult
        {
            Content = shown,
            DryRun = body.DryRun,
            Discarded = !body.DryRun,
            BaseVersion = kept,
            Changes = changes,
            PendingDraft = pending,
        };
    }
}
