using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAccess;
using EPiServer.Web;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary>Taking a branch offline, and deleting an unpublished version.</summary>
internal static class VersionEndpoints
{
    public static WriteResult Unpublish(AgentRequest request, UnpublishRequest body)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.ResolveContent(request.Argument);
        if (ProtectedContent.Contains(ProtectedContent.Links(request.Service<ISiteDefinitionRepository>()), link))
        {
            throw AgentException.Refused($"Content {link.ID} is a site root, start page or asset root; opticli won't take it offline.");
        }
        var content = flow.Locator.LoadAnyLanguage(link);
        if (content is not IVersionable)
        {
            throw AgentException.Usage($"Content {link.ID} ({flow.Types.Load(content.ContentTypeID)?.Name}) has no versions, so it can't be unpublished.",
                "Move it to the recycle bin instead (delete).");
        }
        var language = flow.Locator.ContentLanguage(content, body.Lang);
        var branch = flow.Locator.Versions(link, language);
        var where = language is null ? "" : $" in '{language.Name}'";
        var publishedId = ContentLocator.PublishedVersion(branch)
            ?? throw AgentException.Conflict($"Content {link.ID} isn't published{where}, so there is nothing to take offline.");
        var published = flow.Repository.Get<IContent>(new ContentReference(link.ID, publishedId));
        var what = $"{link.ID} ('{published.Name}')";
        if (published is IVersionable { StopPublish: { } stop } && stop <= DateTime.Now)
        {
            throw AgentException.Conflict($"{what} is already offline{where}: its published version {published.ContentLink} expired {stop.ToUniversalTime():u}.",
                "Publish a version to put it back (publish --version).");
        }
        Approvals.RequireNotInReview(branch, what, language?.Name);
        if (Approvals.Applying(request, link) is not null)
        {
            throw new AgentException(AgentErrorCodes.Refused,
                $"{what} has an approval sequence, so it isn't taken offline directly: that would skip its reviewers.",
                "Send a draft that stops publishing now for review instead: a draft with StopPublish set to now and requestApproval.")
            {
                Reason = AgentErrorReasons.ApprovalSequence,
            };
        }

        // Edit mode opens the common draft; publishing the copy makes it the common draft, so the editor's draft is put back.
        var commonDraft = branch.FirstOrDefault(v => v.IsCommonDraft && v.ContentLink.WorkID > publishedId);
        var before = PropertyValues.Snapshot(published);
        var writable = (IContent)((IReadOnly)published).CreateWritableClone();
        ((IVersionable)writable).StopPublish = DateTime.Now;
        var result = flow.Save(
            writable,
            before,
            SaveAction.Publish | SaveAction.ForceNewVersion,
            body.DryRun,
            ContentSummaries.Describe(published, flow.Types),
            publishedId,
            saveUnchanged: true);
        if (result.Saved && commonDraft is not null)
        {
            flow.Versions.SetCommonDraft(commonDraft.ContentLink);
        }
        return result with
        {
            Published = false,
            Unpublished = result.Saved,
            PreviouslyPublished = result.Saved ? publishedId : null,
        };
    }

    public static WriteResult Discard(AgentRequest request, DiscardRequest body)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.Resolve(request.Argument);
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
        var version = named is { } id
            ? flow.Repository.Get<IContent>(new ContentReference(link.ID, id))
            : flow.Repository.Get<IContent>(ContentLocator.Latest(flow.Locator.Versions(link, language), link, language).ContentLink);
        if (version.ContentLink.ID != link.ID)
        {
            throw AgentException.NotFound($"Content {link.ID} has no version {named}.");
        }
        var versionLanguage = version is ILocalizable { Language: { } own } ? own : null;
        var branch = flow.Locator.Versions(link, versionLanguage);
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
        if (branch.Count == 1)
        {
            throw AgentException.Refused($"{what} is the only version{(versionLanguage is null ? "" : $" of the '{versionLanguage.Name}' branch")}; discarding it would delete the content.",
                "Move the content to the recycle bin instead (delete).");
        }

        // What is lost: the version's values compared with what stays, the published version or else the one before it.
        var kept = ContentLocator.PublishedVersion(branch)
            ?? branch.Where(v => v.ContentLink.WorkID != stamp.ContentLink.WorkID).Select(v => v.ContentLink.WorkID).First();
        var changes = PropertyValues.Diff(PropertyValues.Snapshot(flow.Repository.Get<IContent>(new ContentReference(link.ID, kept))), PropertyValues.Snapshot(version));
        var pending = PendingDrafts.SavedByOptiCli(stamp.SavedBy)
            ? null
            : new PendingDraft(stamp.ContentLink.ToString(), stamp.SavedBy, stamp.Saved.ToUniversalTime(), changes);
        if (pending is not null && !body.IncludeDraft && !body.DryRun)
        {
            throw new AgentException(AgentErrorCodes.Conflict,
                $"{what} was saved by {(string.IsNullOrWhiteSpace(stamp.SavedBy) ? "nobody signed in (a scheduled job or import)" : stamp.SavedBy)}, not opticli; discarding it deletes their changes for good.",
                "Ask whether those changes should be thrown away; if so, retry with includeDraft.")
            {
                PendingDraft = pending,
            };
        }

        var shown = ContentSummaries.Describe(version, flow.Types);
        if (!body.DryRun)
        {
            flow.ThrowIfAborted();
            flow.Versions.Delete(version.ContentLink);
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
