using EPiServer.Core;
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

        var versionId = body.Version ?? (link.WorkID > 0 ? link.WorkID : 0);
        if (versionId == 0)
        {
            var language = flow.Locator.ContentLanguage(flow.Locator.LoadAnyLanguage(link), body.Lang);
            versionId = flow.Locator.LatestVersion(link, language).ContentLink.WorkID;
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

        var writable = (IContent)((IReadOnly)version).CreateWritableClone();
        var issues = ValidationErrors.Validate(flow.Validation, writable, SaveAction.Publish);
        if (ValidationErrors.HasErrors(issues))
        {
            throw AgentException.Invalid(issues);
        }

        var published = flow.Repository.Save(writable, SaveAction.Publish, AccessLevel.NoAccess);
        return new WriteResult
        {
            Content = ContentSummaries.Describe(flow.Repository.Get<IContent>(published), flow.Types),
            Saved = true,
            Published = true,
            BaseVersion = versionId,
            Validation = issues.Count > 0 ? issues : null,
        };
    }
}
