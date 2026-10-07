using EPiServer.Core;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Removes a language branch with all its versions (the agent's <c>POST /v1/content/{ref}/remove-language</c>), never
/// the master language or a start page's.
/// </summary>
internal static class RemoveLanguageOperation
{
    public static RemoveLanguageResult Run(CmsCall call, string reference, RemoveLanguageRequest body)
    {
        var flow = new WriteFlow(call);
        var link = flow.Locator.ResolveContent(reference);
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
        if (Compat.CmsSites.StartPages(call).Any(start => start.CompareToIgnoreWorkID(link)))
        {
            throw AgentException.Refused($"Content {link.ID} is a site's start page; removing its '{culture.Name}' branch takes that language off the site.",
                "Do it in the CMS edit UI if that is intended.");
        }

        // Its variations' versions go with it.
        var branch = flow.Locator.AllVersions(link, culture);
        var result = new RemoveLanguageResult(ContentSummaries.Describe(master, flow.Types), culture.Name, branch.Count,
            ContentLocator.PublishedVersion([.. branch.Where(v => Compat.CmsApi.Variation(v) is null)]) is not null, Removed: !body.DryRun, body.DryRun);
        if (!body.DryRun)
        {
            flow.ThrowIfAborted();
            call.DeleteLanguageBranch(link, culture.Name);
        }
        return result;
    }
}
