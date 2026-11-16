using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;

namespace OptiCli.Agent.Tests.Projects;

/// <summary>An in-memory <see cref="ProjectRepository"/>, for the project rules that need no CMS.</summary>
internal sealed class FakeProjects : ProjectRepository
{
    private int _nextItem = 100;

    public List<Project> Projects { get; } = [];

    public List<ProjectItem> Items { get; } = [];

    public Project Add(int id, string name, ProjectStatus status = ProjectStatus.Active)
    {
        var project = new Project { ID = id, Name = name, Status = status };
        Projects.Add(project);
        return project;
    }

    public ProjectItem AddItem(int project, ContentReference version, string? language)
    {
        var item = new ProjectItem
        {
            ID = _nextItem++,
            ProjectID = project,
            ContentLink = version,
            Language = language is null ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language),
            Category = "default",
        };
        Items.Add(item);
        return item;
    }

    /// <summary>Raises <see cref="ProjectRepository.ProjectSaved"/>, as the CMS does when it saves a project's status.</summary>
    public void RaiseSaved(Project project) => OnProjectSaved(new ProjectEventArgs(project));

    public override Project Get(int id) => Projects.FirstOrDefault(p => p.ID == id)!;

    public override void Save(Project project)
    {
        if (project.ID == 0)
        {
            project.ID = Projects.Select(p => p.ID).DefaultIfEmpty(0).Max() + 1;
        }
        Projects.RemoveAll(p => p.ID == project.ID);
        Projects.Add(project);
    }

    public override void Delete(int id) => Projects.RemoveAll(p => p.ID == id);

    public override IEnumerable<Project> List(ProjectStatus? status, int startIndex, int maxRows, out int totalCount)
    {
        var listed = Projects.Where(p => status is null || p.Status == status).ToList();
        totalCount = listed.Count;
        return listed.Skip(startIndex).Take(maxRows);
    }

    public override ProjectItem GetItem(int id) => Items.FirstOrDefault(i => i.ID == id)!;

    public override IEnumerable<ProjectItem> ListItems(int id, string category, CultureInfo language, int startIndex, int maxRows, out int totalCount)
    {
        var listed = Items.Where(i => i.ProjectID == id).ToList();
        totalCount = listed.Count;
        return listed.Skip(startIndex).Take(maxRows);
    }

    public override IEnumerable<ProjectItem> GetItems(IEnumerable<ContentReference> contentReferences) =>
        contentReferences.SelectMany(link => Items.Where(i => i.ContentLink.ID == link.ID && (link.WorkID == 0 || i.ContentLink.WorkID == link.WorkID))).ToList();

    public override void SaveItems(IEnumerable<ProjectItem> projectItems)
    {
        foreach (var item in projectItems)
        {
            if (item.ID == 0)
            {
                item.ID = _nextItem++;
            }
            Items.RemoveAll(i => i.ID == item.ID);
            Items.Add(item);
        }
    }

    public override void DeleteItems(IEnumerable<int> projectItems)
    {
        var ids = projectItems.ToHashSet();
        Items.RemoveAll(i => ids.Contains(i.ID));
    }
}
