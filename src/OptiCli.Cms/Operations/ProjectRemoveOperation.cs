using EPiServer.Security;
using OptiCli.Cms.Content;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Takes items out of a project (the agent's <c>POST /v1/projects/{project}/remove</c>), as the edit UI's "Remove from
/// Project": the items go, their versions stay as they are. Only from an active project: a published one keeps its items
/// as the record of what it published, and a scheduled one is reactivated first.
/// </summary>
/// <remarks>
/// An editor takes out only items they could put in: content they can read and edit, in a language they may edit, as
/// marking ready asks. Every item is checked before any is taken out; one they can't is a refusal that changes nothing.
/// An item whose version no longer exists has nothing to check. The developer passes every check.
/// </remarks>
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
        // An item of content the editor can't read is, for them, not in the project.
        var selected = ProjectSelection.Select(all.Where(projects.Visible).ToList(), refs.Select(r => (r, locator.Resolve(r))).ToList(), language, ProjectLookup.Describe(project));
        var items = selected.Select(i => Check(call, projects, i)).ToList();
        var result = new ProjectResult { Project = projects.Describe(project, all), EditorUi = ui, DryRun = body.DryRun, Items = items };
        if (items.Any(i => i.Problem is not null))
        {
            return body.DryRun
                ? result with { Valid = false }
                : throw ProjectRules.Refusal("take items out of", ProjectLookup.Describe(project), result, items);
        }
        if (body.DryRun)
        {
            return result;
        }

        projects.ThrowIfAborted();
        projects.Repository.DeleteItems(selected.Select(i => i.Item.ID).ToList());
        return new ProjectResult { Project = projects.Describe(project), EditorUi = ui, Items = items };
    }

    /// <summary>The item to take out, or (for an editor) its problem: no edit access to its content or its language.</summary>
    private static ProjectItemResult Check(CmsCall call, ProjectContext projects, LoadedItem item)
    {
        var removed = new ProjectItemResult { Content = projects.Summary(item), Outcome = ProjectItemOutcomes.Removed };
        if (item.Version is not { } version)
        {
            return removed;
        }
        try
        {
            call.RequireLanguageAccess(version);
            call.RequireAccess(version, AccessLevel.Edit);
            return removed;
        }
        catch (AgentException ex)
        {
            return removed with { Outcome = null, Problem = ProjectRules.Problem(ex, ProjectContext.What(item)) };
        }
    }
}
