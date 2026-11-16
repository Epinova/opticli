using OptiCli.Cms.Content;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Takes items out of a project (the agent's <c>POST /v1/projects/{project}/remove</c>), as the edit UI's "Remove from
/// Project": the items go, their versions stay as they are. Only from an active project: a published one keeps its items
/// as the record of what it published, and a scheduled one is reactivated first.
/// </summary>
internal static class ProjectRemoveOperation
{
    public static ProjectResult Run(CmsCall call, string reference, ProjectRemoveRequest body)
    {
        var refs = body.Items?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? [];
        if (refs.Count == 0)
        {
            throw AgentException.Usage("Give the items to take out of the project.");
        }
        var projects = new ProjectContext(call);
        var ui = projects.RequireEnabled();
        var project = projects.Resolve(reference);
        ProjectRules.RequireActive(call, project, "have items taken out");

        var locator = new ContentLocator(call);
        var language = body.Lang is null ? null : locator.EnabledLanguage(body.Lang);
        var all = projects.Items(project);
        var selected = ProjectSelection.Select(all, refs.Select(r => (r, locator.Resolve(r))).ToList(), language, ProjectLookup.Describe(project));
        var items = selected.Select(i => new ProjectItemResult { Content = projects.Summary(i), Outcome = ProjectItemOutcomes.Removed }).ToList();
        if (body.DryRun)
        {
            return new ProjectResult { Project = projects.Describe(project, all), EditorUi = ui, DryRun = true, Items = items };
        }

        projects.ThrowIfAborted();
        projects.Repository.DeleteItems(selected.Select(i => i.Item.ID).ToList());
        return new ProjectResult { Project = projects.Describe(project), EditorUi = ui, Items = items };
    }
}
