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
        flow.Project = Projects.ProjectSave.For(call, body.Project, body.Ready, body.Publish, publishAt: null);
        // A new branch has no includeDraft (decision 15 of the plan): a takeover over someone else's block draft, if its values
        // ever made one, is refused with no way to confirm it here.
        flow.BlockDraftsConfirmable = false;
        var link = flow.Locator.ResolveContent(reference);
        if (body.WithBlocks is { Count: > 0 } || body.WithoutBlocks is { Count: > 0 })
        {
            // A plan's publish step of the new branch dry-run with the translate before it: the blocks it names, checked in the save.
            flow.Named = flow.Choice(body.WithBlocks, body.WithoutBlocks, "withBlocks", "withoutBlocks", null) with { KeepOthers = body.KeepOthersBlockDrafts };
        }
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

        var action = flow.Decide(link, body.Publish, body.RequestApproval, publishAt: null, $"The '{culture.Name}' branch of {link.ID} ('{master.Name}')");

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
