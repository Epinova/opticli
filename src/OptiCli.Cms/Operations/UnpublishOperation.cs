using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAccess;
using EPiServer.Web;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Takes a branch offline (the agent's <c>POST /v1/content/{ref}/unpublish</c>): its published version is published
/// again as a new version that stops publishing now, so the history shows who took it offline and when.
/// </summary>
internal static class UnpublishOperation
{
    public static WriteResult Run(CmsCall call, string reference, UnpublishRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.ResolveContent(reference);
        if (ProtectedContent.Contains(ProtectedContent.Links(call), link))
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
                call.ForCaller("Publish a version to put it back (publish --version).", "Publish a version to put it back (publish_content with version)."));
        }
        Approvals.RequireNotInReview(branch, what, language?.Name);
        if (Approvals.Applying(call, link) is not null)
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
}
