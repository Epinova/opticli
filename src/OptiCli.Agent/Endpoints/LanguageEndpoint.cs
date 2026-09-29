using EPiServer.Core;
using EPiServer.DataAccess;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class LanguageEndpoint
{
    public static WriteResult Handle(AgentRequest request, LanguageBranchRequest body)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.ResolveContent(request.Argument);
        var culture = flow.Locator.EnabledLanguage(body.Lang);

        var master = flow.Locator.LoadAnyLanguage(link);
        if (master is not ILocalizable localizable)
        {
            throw AgentException.Usage($"Content {link.ID} is not localizable, so it has no language branches.");
        }
        if (localizable.ExistingLanguages.Any(l => l.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw AgentException.Conflict($"Content {link.ID} already has a '{culture.Name}' branch.",
                $"Change it with a draft instead (lang '{culture.Name}').");
        }

        // Compare against what visitors in that language see today: the master language version.
        var before = PropertyValues.Snapshot(master);
        var branch = flow.Repository.CreateLanguageBranch<IContent>(link, culture);
        branch.Name = body.Name ?? master.Name;
        flow.Writer.Apply(branch, body.Properties);

        return flow.Save(
            branch,
            before,
            body.Publish ? SaveAction.Publish : SaveAction.Save,
            body.DryRun,
            ContentSummaries.Describe(master, flow.Types),
            baseVersion: null,
            saveUnchanged: true);
    }
}
