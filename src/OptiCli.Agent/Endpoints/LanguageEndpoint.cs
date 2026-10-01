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

        var action = Approvals.Decide(request, link, body.Publish, body.RequestApproval, $"The '{culture.Name}' branch of {link.ID} ('{master.Name}')");

        // Compare against what visitors in that language see today: the master language version.
        var before = PropertyValues.Snapshot(master);
        var branch = flow.Repository.CreateLanguageBranch<IContent>(link, culture);
        branch.Name = body.Name ?? master.Name;
        flow.Writer.Apply(branch, body.Properties);

        return flow.Save(
            branch,
            before,
            action ?? SaveAction.Save,
            body.DryRun,
            ContentSummaries.Describe(master, flow.Types),
            baseVersion: null,
            saveUnchanged: true);
    }

    public static RemoveLanguageResult Remove(AgentRequest request, RemoveLanguageRequest body)
    {
        var flow = new WriteFlow(request);
        var link = flow.Locator.ResolveContent(request.Argument);
        var master = flow.Locator.LoadAnyLanguage(link);
        if (master is not ILocalizable localizable)
        {
            throw AgentException.Usage($"Content {link.ID} is not localizable, so it has no language branches.");
        }
        var culture = localizable.ExistingLanguages.FirstOrDefault(l => l.Name.Equals(body.Lang, StringComparison.OrdinalIgnoreCase))
            ?? throw AgentException.NotFound($"Content {link.ID} has no '{body.Lang}' branch.",
                $"Its branches: {string.Join(", ", localizable.ExistingLanguages.Select(l => l.Name))}.");
        if (localizable.MasterLanguage.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentException.Refused($"'{culture.Name}' is the master language of {link.ID}; its other branches depend on it.",
                "Delete the content instead (it goes to the recycle bin), or remove the other branches.");
        }
        if (request.Service<EPiServer.Web.ISiteDefinitionRepository>().List().Any(site => site.StartPage.CompareToIgnoreWorkID(link)))
        {
            throw AgentException.Refused($"Content {link.ID} is a site's start page; removing its '{culture.Name}' branch takes that language off the site.",
                "Do it in the CMS edit UI if that is intended.");
        }

        var branch = flow.Locator.Versions(link, culture);
        var result = new RemoveLanguageResult(ContentSummaries.Describe(master, flow.Types), culture.Name, branch.Count,
            ContentLocator.PublishedVersion(branch) is not null, Removed: !body.DryRun, body.DryRun);
        if (!body.DryRun)
        {
            flow.ThrowIfAborted();
            flow.Repository.DeleteLanguageBranch(link, culture.Name, EPiServer.Security.AccessLevel.NoAccess);
        }
        return result;
    }
}
