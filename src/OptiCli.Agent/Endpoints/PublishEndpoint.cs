using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Data.Entity;
using EPiServer.DataAccess;
using EPiServer.Security;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class PublishEndpoint
{
    public static WriteResult Handle(AgentRequest request, PublishRequest body)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.Resolve(request.Argument);
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
        if (version is IVersionable { Status: VersionStatus.Published })
        {
            throw AgentException.Conflict($"Version {version.ContentLink} is already the published version.");
        }

        var versionLanguage = version is ILocalizable { Language: { } own } ? own : null;
        branch ??= flow.Locator.Versions(link, versionLanguage);
        var pending = PendingDrafts.Require(
            flow.Locator.PendingDraft(branch, version), body.IncludeDraft || named is not null, dryRun: false, $"{link.ID} ('{version.Name}')", versionLanguage?.Name);
        var previouslyPublished = ContentLocator.PublishedVersion(branch);

        var writable = (IContent)((IReadOnly)version).CreateWritableClone();
        var issues = ValidationErrors.Validate(flow.Validation, writable, SaveAction.Publish);
        if (ValidationErrors.HasErrors(issues))
        {
            throw AgentException.Invalid(issues);
        }

        flow.ThrowIfAborted();
        ContentReference published;
        string? siteError = null;
        try
        {
            published = flow.Repository.Save(writable, SaveAction.Publish, AccessLevel.NoAccess);
        }
        catch (Exception ex) when (ex is not AgentException && flow.Repository.Get<IContent>(version.ContentLink) is IVersionable { Status: VersionStatus.Published })
        {
            // A handler of the publishing events failed after the version went live (see WriteFlow.Save).
            Console.Error.WriteLine($"[opticli] the site failed after publishing {version.ContentLink}: {ex}");
            published = version.ContentLink;
            siteError = $"{ex.Message} ({ex.GetType().FullName})";
        }
        return new WriteResult
        {
            Content = ContentSummaries.Describe(flow.Repository.Get<IContent>(published), flow.Types),
            SiteError = siteError,
            Saved = true,
            Published = true,
            BaseVersion = versionId,
            Validation = issues.Count > 0 ? issues : null,
            PendingDraft = pending,
            PreviouslyPublished = previouslyPublished,
        };
    }
}
