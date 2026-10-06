using EPiServer.Core;
using EPiServer.DataAccess;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>A new language branch of existing content (the agent's <c>POST /v1/content/{ref}/languages</c>).</summary>
internal static class LanguagesOperation
{
    public static WriteResult Run(CmsCall call, string reference, LanguageBranchRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.ResolveContent(reference);
        var culture = flow.Locator.EnabledLanguage(body.Lang);
        call.RequireLanguageAccess(culture);

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

        var action = Approvals.Decide(call, link, body.Publish, body.RequestApproval, $"The '{culture.Name}' branch of {link.ID} ('{master.Name}')");

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
}
