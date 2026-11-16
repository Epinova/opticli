using System.Globalization;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using Microsoft.Extensions.Logging;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Cancels a scheduled project's publish (the agent's <c>POST /v1/projects/{project}/reactivate</c>) through the CMS's
/// <c>ProjectPublisher.ReactivateAsync</c>, as the edit UI's "Reactivate": the project is active again, and its scheduled
/// items are drafts (<c>checkedOut</c>, the same versions), not ready: publishing the project again needs them marked
/// ready first.
/// </summary>
/// <remarks>
/// For an editor every scheduled item is checked first (read, language and Publish access), as a publish checks its
/// ready items: the CMS checks Publish per item as it goes, so an item the editor may not publish would otherwise leave
/// the items before it unscheduled and the project still scheduled, to go live with only the rest.
/// </remarks>
internal static class ProjectReactivateOperation
{
    public static async Task<ProjectResult> RunAsync(CmsCall call, string reference, ProjectReactivateRequest body)
    {
        var projects = new ProjectContext(call);
        var ui = projects.RequireEnabled();
        var project = projects.Resolve(reference);
        ProjectRules.RequireScheduled(project);
        call.RequirePublishing(EPiServer.DataAccess.SaveAction.Publish);

        var all = projects.Items(project);
        var hidden = all.Count(i => i.State == ProjectItemState.Scheduled && !call.CanRead(i.Version!));
        if (hidden > 0)
        {
            throw AgentException.Refused(
                $"{hidden} scheduled item(s) of project {ProjectLookup.Describe(project)} are content you can't read, so you can't reactivate the project.",
                "Ask someone with access to that content to reactivate the project. Nothing was changed.");
        }
        var scheduled = all.Where(i => i.State == ProjectItemState.Scheduled).ToList();
        var next = new ValidationIssue(null,
            call.ForCaller(
                $"Its scheduled items are drafts again, not ready: mark them ready (opticli projects ready {Id(project)} --all) before publishing the project again.",
                "Its scheduled items are drafts again, not ready: mark them ready (mark_ready_to_publish) before publishing the project again."),
            "warning");
        var result = new ProjectResult
        {
            Project = projects.Describe(project, all),
            EditorUi = ui,
            DryRun = body.DryRun,
            Items = scheduled.Select(i => Check(call, projects, i)).ToList(),
            Validation = [next],
        };
        if (result.Items!.Any(i => i.Problem is not null))
        {
            return body.DryRun
                ? result with { Valid = false }
                : throw ProjectRules.Refusal("reactivate", ProjectLookup.Describe(project), result, result.Items!);
        }
        if (body.DryRun)
        {
            return result;
        }

        projects.ThrowIfAborted();
        string? error = null;
        try
        {
            await call.ReactivateProject(project.CreateWritableClone());
        }
        catch (Exception ex) when (ex is not AgentException)
        {
            error = $"{ex.GetBaseException().Message} ({ex.GetBaseException().GetType().FullName})";
            if (call.OptionalService<ILoggerFactory>() is { } loggers)
            {
                loggers.CreateLogger(CmsCall.LogCategory).LogWarning(ex, "The CMS failed to reactivate project {Project}.", project.ID);
            }
        }
        var now = projects.Repository.Get(project.ID) ?? project;
        var after = projects.Items(now);
        var items = scheduled.Select(before =>
        {
            var item = after.FirstOrDefault(i => i.Item.ID == before.Item.ID) ?? projects.Load(before.Item);
            return item.State == ProjectItemState.Scheduled
                ? new ProjectItemResult { Content = projects.Summary(item), Outcome = ProjectItemOutcomes.Failed, Error = error ?? "The CMS left it scheduled." }
                : new ProjectItemResult { Content = projects.Summary(item), Outcome = ProjectItemOutcomes.Unscheduled };
        }).ToList();
        return result with
        {
            Project = projects.Describe(now, after),
            Reactivated = now.Status == ProjectStatus.Active,
            Items = items,
            SiteError = error,
        };
    }

    /// <summary>The scheduled item to unschedule, or (for an editor) its problem: no Publish access to its content or its language.</summary>
    private static ProjectItemResult Check(CmsCall call, ProjectContext projects, LoadedItem item)
    {
        var unscheduled = new ProjectItemResult { Content = projects.Summary(item), Outcome = ProjectItemOutcomes.Unscheduled };
        try
        {
            call.RequireLanguageAccess(item.Version!);
            call.RequireAccess(item.Version!, AccessLevel.Publish);
            return unscheduled;
        }
        catch (AgentException ex)
        {
            return unscheduled with { Outcome = null, Problem = ProjectRules.Problem(ex, ProjectContext.What(item)) };
        }
    }

    private static string Id(Project project) => project.ID.ToString(CultureInfo.InvariantCulture);
}
