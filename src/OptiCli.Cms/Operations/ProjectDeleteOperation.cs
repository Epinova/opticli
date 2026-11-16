using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Deletes a project (the agent's <c>DELETE /v1/projects/{project}</c>) through <c>ProjectRepository.Delete</c>, as the
/// edit UI's "Delete Project": the project and its items go, their versions stay as they are. Not while it is scheduled
/// (its items would still go live, without a project to reactivate), nor while it is being published or reactivated.
/// </summary>
/// <remarks>The CLI's only: the MCP module has no tool for it (deleting is a follow-up behind <c>AllowDelete</c>).</remarks>
internal static class ProjectDeleteOperation
{
    public static ProjectResult Run(CmsCall call, string reference)
    {
        var projects = new ProjectContext(call);
        var ui = projects.RequireEnabled();
        var project = projects.Resolve(reference);
        ProjectRules.RequireDeletable(call, project);
        call.RequireDeleting();

        var items = projects.Items(project);
        var summary = projects.Describe(project, items);
        projects.ThrowIfAborted();
        projects.Repository.Delete(project.ID);
        return new ProjectResult
        {
            Project = summary,
            EditorUi = ui,
            Deleted = true,
            Items = items.Where(i => i.Version is null || call.CanRead(i.Version)).Select(i => new ProjectItemResult { Content = projects.Summary(i) }).ToList(),
        };
    }
}
