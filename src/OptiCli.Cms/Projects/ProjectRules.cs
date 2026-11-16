using System.Globalization;
using System.Text.Json;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Protocol;

namespace OptiCli.Cms.Projects;

/// <summary>Where an item of a project stands, as the project operations see it.</summary>
internal enum ProjectItemState
{
    /// <summary>A draft (<c>checkedOut</c>), or rejected by a reviewer: can be marked ready.</summary>
    Draft,

    /// <summary>Marked Ready to publish (<c>checkedIn</c>): what a project publish puts live.</summary>
    Ready,

    /// <summary>Awaiting approval: its reviewers decide.</summary>
    InReview,

    /// <summary>Its version is scheduled to be published.</summary>
    Scheduled,

    /// <summary>Its version is the published one, or was.</summary>
    Published,

    /// <summary>The content is in the recycle bin.</summary>
    Deleted,

    /// <summary>The version no longer exists, or the content has no versions.</summary>
    Missing,
}

/// <summary>
/// The rules the project operations share, which need no CMS: what each project status allows (as the edit UI: only
/// <c>active</c> and <c>publishFailed</c> projects take items or publish), and where an item stands.
/// </summary>
internal static class ProjectRules
{
    /// <summary>A project's status as <see cref="ProjectStatuses"/> names it.</summary>
    public static string StatusName(ProjectStatus status) => status switch
    {
        ProjectStatus.Active => ProjectStatuses.Active,
        ProjectStatus.Publishing => ProjectStatuses.Publishing,
        ProjectStatus.PublishFailed => ProjectStatuses.PublishFailed,
        ProjectStatus.Published => ProjectStatuses.Published,
        ProjectStatus.DelayedPublished => ProjectStatuses.DelayedPublished,
        ProjectStatus.Reactivating => ProjectStatuses.Reactivating,
        _ => JsonNamingPolicy.CamelCase.ConvertName(status.ToString()),
    };

    /// <summary>Whether the project takes changes to its items (saves into it, ready, remove) and publishes.</summary>
    public static bool IsActive(ProjectStatus status) => status is ProjectStatus.Active or ProjectStatus.PublishFailed;

    /// <summary>For a save into the project, marking items ready and removing them: only an active project.</summary>
    /// <param name="change">What the request does, for the message: "take items", "mark items ready in", ...</param>
    /// <exception cref="AgentException"><c>conflict</c> with <see cref="AgentErrorReasons.ProjectNotActive"/>.</exception>
    public static void RequireActive(CmsCall call, Project project, string change)
    {
        if (!IsActive(project.Status))
        {
            throw NotActive(call, project, $"can't {change}");
        }
    }

    /// <summary>For reactivate: only a scheduled project.</summary>
    /// <exception cref="AgentException"><c>conflict</c> with <see cref="AgentErrorReasons.ProjectNotActive"/>.</exception>
    public static void RequireScheduled(Project project)
    {
        if (project.Status != ProjectStatus.DelayedPublished)
        {
            throw new AgentException(AgentErrorCodes.Conflict,
                $"Project {ProjectLookup.Describe(project)} isn't scheduled to be published, so there is nothing to reactivate.",
                "Only a scheduled project (delayedPublished) is reactivated; an active one takes changes as it is. Nothing was changed.")
            {
                Reason = AgentErrorReasons.ProjectNotActive,
            };
        }
    }

    /// <summary>
    /// For delete: not while it is scheduled (its items would still go live, without the project), nor while it is being
    /// published or reactivated.
    /// </summary>
    /// <exception cref="AgentException"><c>conflict</c> with <see cref="AgentErrorReasons.ProjectNotActive"/>.</exception>
    public static void RequireDeletable(CmsCall call, Project project)
    {
        if (project.Status is ProjectStatus.DelayedPublished or ProjectStatus.Publishing or ProjectStatus.Reactivating)
        {
            throw NotActive(call, project, "be deleted");
        }
    }

    private static AgentException NotActive(CmsCall call, Project project, string cant)
    {
        var what = ProjectLookup.Describe(project);
        var id = project.ID.ToString(CultureInfo.InvariantCulture);
        var (message, hint) = project.Status switch
        {
            ProjectStatus.Published => ($"Project {what} is published, so it {cant}.",
                call.ForCaller("Create a new project for further changes (POST /v1/projects, opticli projects create). Nothing was changed.",
                    "Create a new project for further changes (create_project). Nothing was changed.")),
            ProjectStatus.DelayedPublished => ($"Project {what} is scheduled to be published{At(project)}, so it {cant}.",
                call.ForCaller($"Reactivate it first (opticli projects reactivate {id}), which cancels the scheduled publish; its items are then drafts to mark ready again. Nothing was changed.",
                    $"Ask the user whether to cancel the scheduled publish (reactivate_project {id}, where the site lets assistants publish; else the user reactivates it in the CMS); its items are then drafts to mark ready again. Nothing was changed.")),
            ProjectStatus.Publishing => ($"Project {what} is being published right now, so it {cant}.", "Try again once the publish is done. Nothing was changed."),
            ProjectStatus.Reactivating => ($"Project {what} is being reactivated right now, so it {cant}.", "Try again once that is done. Nothing was changed."),
            _ => ($"Project {what} {cant} in its status.", "Nothing was changed."),
        };
        return new AgentException(AgentErrorCodes.Conflict, message, hint) { Reason = AgentErrorReasons.ProjectNotActive };
    }

    private static string At(Project project) =>
        project.DelayPublishUntil is { } at ? $" at {at.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}" : "";

    /// <summary>Where an item stands, from its version's status.</summary>
    /// <param name="status">The version's status; null when the version doesn't exist (or the content has no versions).</param>
    /// <param name="deleted">The content is in the recycle bin.</param>
    public static ProjectItemState State(VersionStatus? status, bool deleted) =>
        deleted ? ProjectItemState.Deleted : status switch
        {
            VersionStatus.CheckedOut or VersionStatus.Rejected => ProjectItemState.Draft,
            VersionStatus.CheckedIn => ProjectItemState.Ready,
            VersionStatus.AwaitingApproval => ProjectItemState.InReview,
            VersionStatus.DelayedPublish => ProjectItemState.Scheduled,
            VersionStatus.Published or VersionStatus.PreviouslyPublished => ProjectItemState.Published,
            _ => ProjectItemState.Missing,
        };

    /// <summary>What a project publish does with an item in <paramref name="state"/>: one of <see cref="ProjectItemOutcomes"/>.</summary>
    public static string PublishOutcome(ProjectItemState state, bool scheduling) => state switch
    {
        ProjectItemState.Ready => scheduling ? ProjectItemOutcomes.Scheduled : ProjectItemOutcomes.Published,
        ProjectItemState.Published => ProjectItemOutcomes.AlreadyPublished,
        _ => ProjectItemOutcomes.NotReady,
    };

    /// <summary>
    /// Why an item named for marking ready can't be; null when it can (a draft) or is ready already. With
    /// <see cref="ProjectReadyRequest.All"/> such items are skipped instead.
    /// </summary>
    /// <param name="what">The item, e.g. <c>123 ('News') in 'en'</c>.</param>
    public static ProjectItemProblem? CantMarkReady(ProjectItemState state, string what) => state switch
    {
        ProjectItemState.Draft or ProjectItemState.Ready => null,
        ProjectItemState.InReview => new(AgentErrorCodes.Conflict, $"{what} is in review: a reviewer must approve or reject it in the CMS edit UI.", AgentErrorReasons.InReview),
        ProjectItemState.Scheduled => new(AgentErrorCodes.Conflict, $"{what} is scheduled to be published already."),
        ProjectItemState.Published => new(AgentErrorCodes.Conflict, $"{what} is a published version, so there is nothing to mark ready: save a new draft into the project."),
        ProjectItemState.Deleted => new(AgentErrorCodes.Conflict, $"{what} is in the recycle bin."),
        _ => new(AgentErrorCodes.NotFound, $"{what}: the project's version of it no longer exists."),
    };

    /// <summary>
    /// The refusal of a project operation over its items' problems, carrying <paramref name="result"/> (every item, with its
    /// problem). Its code is that of the most serious problem: <c>refused</c>, then <c>validation</c>, <c>conflict</c>,
    /// <c>not_found</c> and <c>usage</c>. Its reason is <see cref="AgentErrorReasons.ProjectItems"/>, unless every problem has
    /// the same reason, which is then the error's: another person's draft (the conflict <c>includeDraft</c> confirms), an
    /// approval sequence (<c>requestApproval</c>), and so on.
    /// </summary>
    /// <param name="action">What was asked, for the message: "publish", "mark items ready in".</param>
    /// <param name="project">The project, for the message.</param>
    /// <param name="approvalHint">
    /// The hint when every problem is an approval sequence, for a request that takes no <c>requestApproval</c> (a
    /// publish, whose items were marked ready past their reviewers); null: retry with <c>requestApproval</c>.
    /// </param>
    public static AgentException Refusal(string action, string project, ProjectResult result, IReadOnlyList<ProjectItemResult> items, string? approvalHint = null)
    {
        var problems = items.Where(i => i.Problem is not null).ToList();
        var codes = problems.Select(p => p.Problem!.Code).ToHashSet();
        var code = Severity.FirstOrDefault(codes.Contains) ?? AgentErrorCodes.Conflict;
        // One reason for every problem is the error's own, which a caller acts on as for a single item (includeDraft,
        // requestApproval); mixed problems are projectItems.
        var reasons = problems.Select(p => p.Problem!.Reason).Distinct().ToList();
        var shared = reasons is [{ } only] ? only : null;
        var pendingOnly = shared == AgentErrorReasons.PendingDraft;
        var listed = string.Join("; ", problems.Take(5).Select(p => p.Problem!.Message));
        var more = problems.Count > 5 ? $"; and {problems.Count - 5} more" : "";
        var message = $"Can't {action} project {project}: {problems.Count} of {items.Count} item(s) can't go ahead. {listed}{more}";
        var hint = pendingOnly
            ? "Ask whether those changes should go live too; if so, retry with includeDraft. Nothing was changed."
            : shared == AgentErrorReasons.ApprovalSequence
                ? approvalHint ?? "Retry with requestApproval to send those items for review instead (their reviewers then approve them). Nothing was changed."
                : "Each item's problem is in error.project; fix them (or leave those items out) and retry. Nothing was changed.";
        return new AgentException(code, message, hint)
        {
            Reason = shared ?? AgentErrorReasons.ProjectItems,
            PendingDraft = pendingOnly ? problems.Select(p => p.PendingDraft).FirstOrDefault(d => d is not null) : null,
            Validation = problems.SelectMany(p => p.Validation ?? []).ToList() is { Count: > 0 } issues ? issues : null,
            Project = result with { Valid = false },
        };
    }

    private static readonly string[] Severity =
        [AgentErrorCodes.Refused, AgentErrorCodes.Validation, AgentErrorCodes.Conflict, AgentErrorCodes.NotFound, AgentErrorCodes.Usage];

    /// <summary>The problem an item has from an <see cref="AgentException"/> an item check threw.</summary>
    public static ProjectItemProblem Problem(AgentException exception, string what) =>
        new(exception.Code, exception.Message.StartsWith(what, StringComparison.Ordinal) ? exception.Message : $"{what}: {exception.Message}",
            exception.Reason ?? (exception.PendingDraft is null ? null : AgentErrorReasons.PendingDraft));
}
