using EPiServer.DataAbstraction;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>A new, active project (the agent's <c>POST /v1/projects</c>), as the edit UI's "New Project" makes one.</summary>
internal static class ProjectCreateOperation
{
    public static ProjectResult Run(CmsCall call, ProjectCreateRequest body)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw AgentException.Usage("name is required.");
        }
        if (name.Length > ProjectCreateRequest.MaxNameLength)
        {
            throw AgentException.Usage($"The name is {name.Length} characters; the CMS keeps at most {ProjectCreateRequest.MaxNameLength}.");
        }
        var projects = new ProjectContext(call);
        var ui = projects.RequireEnabled();
        if (ProjectLookup.Taken(projects.Repository.List(), name) is { } taken)
        {
            throw AgentException.Conflict(
                $"Project {ProjectLookup.Describe(taken)} has that name already, and projects are named by their name too.",
                call.ForCaller($"Save into that one (project {taken.ID}), or give the new project another name.", $"Save into that one (project {taken.ID}), or give the new project another name."));
        }

        var project = new Project { Name = name, Status = ProjectStatus.Active };
        if (body.DryRun)
        {
            return new ProjectResult
            {
                Project = new ProjectSummary { Id = 0, Name = name, Status = ProjectStatuses.Active, CreatedBy = call.UserName },
                EditorUi = ui,
                DryRun = true,
            };
        }
        projects.ThrowIfAborted();
        // The repository fills in when and by whom (the current principal: opticli for the developer, the editor's own).
        projects.Repository.Save(project);
        var saved = projects.Repository.Get(project.ID) ?? project;
        return new ProjectResult { Project = projects.Describe(saved, []), EditorUi = ui, Created = true };
    }
}
