using System.Text.Json;
using EPiServer.Core;
using EPiServer.Data.Entity;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class DraftEndpoint
{
    public static WriteResult Handle(AgentRequest request, DraftRequest body)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.Resolve(request.Argument);
        var language = flow.Locator.ContentLanguage(flow.Locator.LoadAnyLanguage(link), body.Lang);

        // Optimistic concurrency: the caller must have seen the latest version.
        var latest = flow.Locator.LatestVersion(link, language);
        if (body.BaseVersion is { } expected && expected != latest.ContentLink.WorkID)
        {
            throw new AgentException(
                AgentErrorCodes.Conflict,
                $"Version {expected} is not the latest version of {link.ID}{(language is null ? "" : $" in '{language.Name}'")}; {latest.ContentLink} is ({JsonNamingPolicy.CamelCase.ConvertName(latest.Status.ToString())}, saved {latest.Saved.ToUniversalTime():u} by {latest.SavedBy}).",
                $"Re-read {latest.ContentLink}, reapply the change and retry with baseVersion {latest.ContentLink.WorkID}.")
            {
                CurrentVersion = latest.ContentLink.WorkID,
            };
        }

        var baseLink = link.WorkID > 0 ? link : latest.ContentLink;
        var current = flow.Repository.Get<IContent>(baseLink);
        if (current.ContentLink.ID != link.ID)
        {
            throw AgentException.NotFound($"Content {link.ID} has no version {baseLink.WorkID}.");
        }
        if (language is not null && current is ILocalizable { Language: { } versionLanguage } && !versionLanguage.Name.Equals(language.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentException.Usage($"Version {baseLink} is in '{versionLanguage.Name}', not '{language.Name}'.");
        }

        var before = PropertyValues.Snapshot(current);
        var writable = (IContent)((IReadOnly)current).CreateWritableClone();
        if (body.Name is { } name)
        {
            writable.Name = name;
        }
        flow.Writer.Apply(writable, body.Properties);
        flow.Areas.Apply(writable, body.AreaOps);

        return flow.Save(
            writable,
            before,
            WriteFlow.DraftAction(body.Publish),
            body.DryRun,
            ContentSummaries.Describe(current, flow.Types),
            baseLink.WorkID,
            saveUnchanged: body.Publish);
    }
}
