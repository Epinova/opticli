using System.Text.Json;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using Microsoft.Extensions.Logging;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Reading projects through the CMS's own <c>ProjectRepository</c>: the MCP module's <c>list_projects</c>,
/// <c>get_project</c> and the <c>projects</c> of <c>get_content</c>. The CLI reads them from the database instead
/// (<c>ProjectReader</c>), so the agent has no routes for these.
/// </summary>
/// <remarks>
/// A project has no access rights of its own (every editor sees every project, as in the edit UI); of its items an
/// editor sees only content they can read, and the counts are of those (<see cref="ProjectContext.Describe"/>). Reads
/// aren't refused where editors have no projects: <see cref="ProjectEditorUi.None"/> says so.
/// </remarks>
internal static class ProjectReadOperation
{
    /// <summary>The most projects listed at once, and how many when no limit is given.</summary>
    public const int MaxLimit = 200, DefaultLimit = 50;

    /// <param name="all">Published projects too; only the others (active, scheduled, failed, busy) when false.</param>
    /// <param name="limit">At most this many, newest first.</param>
    public static ProjectList List(CmsCall call, bool all, int? limit)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw AgentException.Usage($"limit is from 1 to {MaxLimit}.");
        }
        var projects = new ProjectContext(call);
        var matching = projects.Repository.List()
            .Where(p => all || p.Status != ProjectStatus.Published)
            .OrderByDescending(p => p.ID)
            .ToList();
        var take = limit ?? DefaultLimit;
        return new ProjectList
        {
            Projects = matching.Take(take).Select(p => projects.Describe(p)).ToList(),
            EditorUi = EditorUi(call),
            More = matching.Count > take,
        };
    }

    /// <param name="reference">The project's id or exact name (<see cref="ProjectLookup"/>).</param>
    public static ProjectDetails Get(CmsCall call, string reference)
    {
        var projects = new ProjectContext(call);
        var project = projects.Resolve(reference);
        var items = projects.Items(project);
        return new ProjectDetails
        {
            Project = projects.Describe(project, items),
            EditorUi = EditorUi(call),
            Items = items.Where(projects.Visible).Select(i => new ProjectItemEntry(projects.Summary(i), StateName(i.State))).ToList(),
        };
    }

    /// <summary>
    /// The projects that hold a version of <paramref name="content"/> (any language), oldest project first, as the CLI's
    /// <c>get</c> lists them; null when none does. The caller has checked that the content is theirs to read. A failure
    /// to read projects is logged and gives null: it never fails the read it goes with.
    /// </summary>
    public static IReadOnlyList<ContentProject>? Of(CmsCall call, ContentReference content)
    {
        try
        {
            var projects = new ProjectContext(call);
            var held = projects.Repository.GetItems([content.ToReferenceWithoutVersion()])
                .Select(i => (Item: i, Project: projects.Repository.Get(i.ProjectID)))
                .Where(p => p.Project is not null)
                .OrderBy(p => p.Project!.ID)
                .ThenBy(p => p.Item.ID)
                .Select(p => new ContentProject(
                    p.Project!.ID,
                    p.Project.Name,
                    ProjectRules.StatusName(p.Project.Status),
                    p.Item.ContentLink.WorkID > 0 ? p.Item.ContentLink.ToString() : null,
                    ProjectContext.LanguageName(p.Item.Language)))
                .ToList();
            return held.Count > 0 ? held : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            call.OptionalService<ILoggerFactory>()?.CreateLogger(CmsCall.LogCategory).LogWarning(ex, "opticli couldn't read the projects of content {Content}.", content.ID);
            return null;
        }
    }

    /// <summary>A state as <see cref="ProjectItemStates"/> names it.</summary>
    public static string StateName(ProjectItemState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());

    /// <summary>Which project UI editors have: <see cref="ProjectEditorUi"/>, <see cref="ProjectEditorUi.None"/>, or null when it can't be told.</summary>
    private static string? EditorUi(CmsCall call) => Compat.CmsProjectUi.Of(call.Services);
}
