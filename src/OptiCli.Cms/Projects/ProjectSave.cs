using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using Microsoft.Extensions.Logging;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Projects;

/// <summary>
/// A save into a project (<see cref="DraftRequest.Project"/> and the same field on the other save requests), for
/// <see cref="WriteFlow"/>: before the save, whether the content may go in; after it, the saved version put in the
/// project in place of the content's earlier version in that language, and marked Ready to publish if asked.
/// </summary>
/// <remarks>
/// <para>As the edit UI's save in project mode (<c>ContentEventsListener</c>): one version per content item and language
/// in a project (the newer replaces the older, which stays as it is), not content that is in another project, and the
/// item's category <c>default</c>. Unlike the edit UI, a folder or content without versions is refused rather than left
/// out, and so is content in another project that isn't published (the edit UI only checks the version it saved).</para>
/// <para>Marking ready is a <c>SaveAction.CheckIn</c> of the saved version, which keeps its version id (so the item needs
/// no update) and needs Edit access. Under an approval sequence it is refused: CMS 12 would let the version past its
/// reviewers (a project publish then puts it live), CMS 13 refuses it itself; <c>requestApproval</c> sends it for review
/// instead.</para>
/// </remarks>
internal sealed class ProjectSave
{
    private readonly ProjectContext _projects;

    private readonly Project _project;

    private readonly string? _editorUi;

    private ProjectSave(ProjectContext projects, Project project, bool ready, string? editorUi)
    {
        _projects = projects;
        _project = project;
        Ready = ready;
        _editorUi = editorUi;
    }

    /// <summary>The saved version is to be marked Ready to publish.</summary>
    public bool Ready { get; }

    /// <summary>The request asked for a review (<c>requestApproval</c>), as <see cref="RequestApproval"/> was given it.</summary>
    private bool _reviewRequested;

    private CmsCall Call => _projects.Call;

    /// <summary>The project a save request names, checked; null for a save without one.</summary>
    /// <param name="publish">The request publishes (<c>publish</c>), or <paramref name="publishAt"/> schedules: not with a project.</param>
    /// <exception cref="AgentException">
    /// <c>usage</c> for <c>ready</c> without a project, or a project with a publish; <c>refused</c>
    /// (<see cref="AgentErrorReasons.ProjectsDisabled"/>); <c>not_found</c>; <c>conflict</c> for a project that isn't active.
    /// </exception>
    public static ProjectSave? For(CmsCall call, string? project, bool ready, bool publish, DateTime? publishAt)
    {
        if (project is null)
        {
            return ready
                ? throw AgentException.Usage("ready marks the saved version Ready to publish in a project, so it needs project too.",
                    call.ForCaller("Give the project (--in-project) with --ready, or leave --ready out.", "Give project with ready, or leave ready out."))
                : null;
        }
        if (publish || publishAt is not null)
        {
            throw AgentException.Usage(
                "A save into a project isn't published or scheduled by itself: the project's publish puts its ready items live together.",
                call.ForCaller(
                    "Leave out publish and publishAt (--publish, --publish-at), and publish or schedule the project once its items are ready (opticli projects publish). Nothing was saved.",
                    "Leave out publish and publishAt; publish_project puts the project's ready items live together, when the user asks for it. Nothing was saved."));
        }
        var projects = new ProjectContext(call);
        var ui = projects.RequireEnabled();
        var found = projects.Resolve(project);
        ProjectRules.RequireActive(call, found, "take items");
        return new ProjectSave(projects, found, ready, ui);
    }

    /// <summary>
    /// The request's <c>requestApproval</c> as the save's publish decision takes it (<see cref="WriteFlow.Decide"/>). With
    /// <see cref="Ready"/>: where an approval sequence applies, the version must go for review instead (refused without
    /// <paramref name="requested"/>); elsewhere it is marked ready after the save, and there is no review to request.
    /// </summary>
    /// <param name="link">The content, or the parent of new content.</param>
    /// <exception cref="AgentException"><c>refused</c> with <see cref="AgentErrorReasons.ApprovalSequence"/>.</exception>
    public bool RequestApproval(ContentReference link, bool requested, string what)
    {
        _reviewRequested = requested;
        if (!Ready)
        {
            return requested;
        }
        if (Approvals.Applying(Call, link) is null)
        {
            return false;
        }
        return requested ? true : throw new AgentException(AgentErrorCodes.Refused,
            $"{what} has an approval sequence, so it isn't marked Ready to publish: that would let it past its reviewers.",
            Call.ForCaller(
                "Retry with requestApproval (--request-approval) to send it for review instead; it goes in the project all the same. Without ready it is saved in the project as a draft. Nothing was saved.",
                "Retry with requestApproval to send it for review instead; it goes in the project all the same. Without ready it is saved in the project as a draft. Nothing was saved."))
        {
            Reason = AgentErrorReasons.ApprovalSequence,
        };
    }

    /// <summary>Before the save (a dry run's too): whether <paramref name="writable"/> may go in the project.</summary>
    /// <exception cref="AgentException">
    /// <c>usage</c> for a folder or content without versions; <c>conflict</c> (<see cref="AgentErrorReasons.InOtherProject"/>)
    /// for content in another project that isn't published, in the same language.
    /// </exception>
    public void Check(IContent writable)
    {
        var what = What(writable);
        if (writable is ContentFolder or not IVersionable)
        {
            throw AgentException.Usage(
                $"{what} {(writable is ContentFolder ? "is a folder" : "has no versions")}, so it can't be in a project (nor can it in the CMS edit UI).",
                "Save it without project. Nothing was saved.");
        }
        if (ContentReference.IsNullOrEmpty(writable.ContentLink))
        {
            return;
        }
        // The project's items change even when nothing is saved (a version put in as it is, replacing the item there):
        // only for someone who may edit the content, as removing it from the project asks. A save checks it again.
        Call.RequireAccess(writable, EPiServer.Security.AccessLevel.Edit);
        if (_projects.InOtherProjects(writable.ContentLink, ProjectContext.Language(writable), _project.ID) is [var (item, other), ..])
        {
            var id = other.ID.ToString(CultureInfo.InvariantCulture);
            throw new AgentException(AgentErrorCodes.Conflict,
                $"{what} is in project {ProjectLookup.Describe(other)} already (version {item.ContentLink}), and content isn't put in two projects (the CMS edit UI doesn't either).",
                Call.ForCaller(
                    $"Save it into that project instead (project {id}), or take it out of that one first (opticli projects remove {id} {writable.ContentLink.ID}). Nothing was saved.",
                    $"Save it into that project instead (project {id}), or ask the user whether to take it out of that one first (remove_from_project). Nothing was saved."))
            {
                Reason = AgentErrorReasons.InOtherProject,
            };
        }
    }

    /// <summary>What a dry run reports: what the save would do to the project.</summary>
    /// <param name="review">The save sends the version for review (<c>RequestApproval</c>), so it isn't marked ready.</param>
    /// <param name="intoVersion">
    /// The version the save goes into instead of a new one, when it does; or, for a write with nothing to save, the
    /// version it is based on, which goes in as it is (<see cref="AddUnchanged"/>).
    /// </param>
    public ProjectSaveResult Planned(IContent writable, bool review, int? intoVersion)
    {
        var mine = ContentReference.IsNullOrEmpty(writable.ContentLink) ? [] : Mine(writable.ContentLink, ProjectContext.Language(writable));
        return Result(
            intoVersion is { } into && mine.Any(i => i.ContentLink.WorkID == into) ? ProjectSaveItems.Unchanged
                : mine.Count > 0 ? ProjectSaveItems.Replaced : ProjectSaveItems.Added,
            mine.Select(i => i.ContentLink.ToString()).FirstOrDefault(),
            // Another person's draft, put in as it is, isn't marked ready (AddUnchanged).
            ready: Ready && !review && !(intoVersion == writable.ContentLink.WorkID && writable is IChangeTrackable { ChangedBy: var by } && !PendingDrafts.SavedBy(by, Call.UserName)));
    }

    /// <summary>
    /// After the save: <paramref name="saved"/> (the version as stored) put in the project in place of the content's
    /// earlier version in its language, then marked ready if asked. The save stands whatever happens here: a failure is
    /// reported in <see cref="ProjectSaveResult.Error"/> and the site's log.
    /// </summary>
    /// <param name="action">How it was saved: a version sent for review isn't marked ready.</param>
    /// <remarks>Marking ready is a save of its own: take the save's site events before calling this.</remarks>
    /// <param name="markReady">False to leave <see cref="Ready"/> out for this version (<see cref="AddUnchanged"/>).</param>
    public ProjectSaveResult Add(IContent saved, SaveAction action, bool markReady = true)
    {
        var language = ProjectContext.Language(saved);
        string item;
        string? replaced = null;
        try
        {
            var mine = Mine(saved.ContentLink, language);
            if (mine.Any(i => i.ContentLink.WorkID == saved.ContentLink.WorkID))
            {
                item = ProjectSaveItems.Unchanged;
            }
            else
            {
                // The new item first, then the old ones: the project is never without the content.
                _projects.Repository.SaveItems([new ProjectItem
                {
                    ProjectID = _project.ID,
                    ContentLink = saved.ContentLink,
                    Language = language,
                    Category = ProjectContext.ItemCategory,
                }]);
                if (mine.Count > 0)
                {
                    replaced = mine[0].ContentLink.ToString();
                    _projects.Repository.DeleteItems(mine.Select(i => i.ID).ToList());
                }
                item = mine.Count > 0 ? ProjectSaveItems.Replaced : ProjectSaveItems.Added;
            }
        }
        catch (Exception ex)
        {
            return Failed(saved, ex, ProjectSaveItems.None, null);
        }

        if (!Ready || !markReady || WriteFlow.Kind(action) == SaveAction.RequestApproval || saved is not IVersionable { Status: VersionStatus.CheckedOut or VersionStatus.Rejected })
        {
            return Result(item, replaced, ready: saved is IVersionable { Status: VersionStatus.CheckedIn });
        }
        try
        {
            Call.Save((IContent)((IReadOnly)saved).CreateWritableClone(), SaveAction.CheckIn);
        }
        catch (Exception ex)
        {
            return Failed(saved, ex, item, replaced);
        }
        // A read racing the check-in (the edit UI's project overview) can leave CMS 12's cache with the version as a draft
        // (ProjectContext.Load): dropped, so the result reads it as stored. A project operation reads its items fresh anyway.
        _projects.Forget(saved.ContentLink);
        return Result(item, replaced, ready: true);
    }

    /// <summary>
    /// For a block of the content's "For this page" folder that the write makes or changes, and saves into the project with
    /// the content (<see cref="SharedAreaBlocks"/>): how it follows the content, when the content is marked ready or sent
    /// for review, so that "use a CMS project" puts the content and its blocks live together. Marked ready
    /// (<see cref="SaveAction.CheckIn"/>) where no approval sequence applies to the block; sent for review
    /// (<see cref="SaveAction.RequestApproval"/>) where one does and the request asked for a review. Checked before
    /// anything is saved (a dry run's too).
    /// </summary>
    /// <param name="link">The block, or for a new block where it goes (the folder, or the content it inherits from).</param>
    /// <param name="ownerAction">The content's save action (<see cref="WriteFlow.Decide"/>).</param>
    /// <returns>Null when the content is neither marked ready nor sent for review: the block stays a draft in the project.</returns>
    /// <exception cref="AgentException">
    /// <c>refused</c> (<see cref="AgentErrorReasons.ApprovalSequence"/>) for <c>ready</c> without <c>requestApproval</c> where
    /// a sequence applies to the block, as for the content itself.
    /// </exception>
    public SaveAction? BlockAction(ContentReference link, SaveAction ownerAction, string what)
    {
        if (!Ready && WriteFlow.Kind(ownerAction) != SaveAction.RequestApproval)
        {
            return null;
        }
        if (Approvals.Applying(Call, link) is null)
        {
            return SaveAction.CheckIn;
        }
        return _reviewRequested ? SaveAction.RequestApproval : throw new AgentException(AgentErrorCodes.Refused,
            $"The block at {what} has an approval sequence, so it isn't marked Ready to publish with the content: that would let it past its reviewers.",
            Call.ForCaller(
                "Retry with requestApproval (--request-approval) to send it for review with the content instead; or without ready, to save both in the project as drafts. Nothing was saved.",
                "Retry with requestApproval to send it for review with the content instead; or without ready, to save both in the project as drafts. Nothing was saved."))
        {
            Reason = AgentErrorReasons.ApprovalSequence,
        };
    }

    /// <summary>
    /// After the content's save: a block that went in the project as a draft with it, marked ready or sent for review as
    /// <see cref="BlockAction"/> decided (a save of the block's version that keeps its id, as the edit UI's "Ready to
    /// Publish" and "Ready for Review" do).
    /// </summary>
    /// <returns>Why the CMS didn't take it; null once done.</returns>
    public string? Follow(IContent saved, SaveAction action)
    {
        try
        {
            Call.Save((IContent)((IReadOnly)saved).CreateWritableClone(), action);
        }
        catch (Exception ex)
        {
            if (Call.OptionalService<ILoggerFactory>() is { } loggers)
            {
                loggers.CreateLogger(CmsCall.LogCategory).LogWarning(ex, "Saved {Content} in project {Project}, but couldn't {Action} it.", saved.ContentLink.ToString(), _project.ID, action);
            }
            return ex is AgentException ? ex.Message : $"{ex.Message} ({ex.GetType().FullName})";
        }
        _projects.Forget(saved.ContentLink);
        return null;
    }

    /// <summary>
    /// A write that changed nothing, so saved nothing: the version it was based on (<paramref name="based"/>) is put in the
    /// project when it is a draft (and marked ready if asked), as a plan run again expects. A published version is left
    /// out, with a warning.
    /// </summary>
    /// <param name="action">The write's decided save action (<see cref="WriteFlow.Decide"/>): a review request isn't marked ready.</param>
    public (ProjectSaveResult Result, ValidationIssue? Warning) AddUnchanged(IContent based, SaveAction action)
    {
        var state = ProjectRules.State((based as IVersionable)?.Status, based.IsDeleted);
        if (Ready && state == ProjectItemState.Draft && based is IChangeTrackable { ChangedBy: var by } && !PendingDrafts.SavedBy(by, Call.UserName))
        {
            // Checking another person's draft in would record the caller as the one who saved it, and the project's publish
            // would no longer ask about their changes (ProjectReadyOperation asks for includeDraft for that).
            var id = _project.ID.ToString(CultureInfo.InvariantCulture);
            return (Add(based, action, markReady: false), new ValidationIssue(null,
                $"Nothing changed, so nothing was saved, and version {based.ContentLink} is a draft {(string.IsNullOrWhiteSpace(by) ? "saved without a user name (a scheduled job or import)" : $"{by} saved")}: it was put in project {ProjectLookup.Describe(_project)}, but not marked ready, since that would record you as the one who saved it and publishing the project wouldn't ask about those changes. "
                + Call.ForCaller(
                    $"If they should go live, mark it ready with opticli projects ready {id} {based.ContentLink} --include-draft.",
                    "If the user wants those changes to go live, mark it ready with mark_ready_to_publish and includeDraft."),
                "warning"));
        }
        if (state is ProjectItemState.Draft or ProjectItemState.Ready or ProjectItemState.InReview)
        {
            return (Add(based, action), null);
        }
        return (Result(ProjectSaveItems.None, null, ready: false), new ValidationIssue(null,
            $"Nothing changed, so nothing was saved, and version {based.ContentLink} isn't a draft, so it wasn't put in project {ProjectLookup.Describe(_project)}: save a change to make a draft for it.",
            "warning"));
    }

    /// <summary>
    /// For a save without a project, of content that is in one (in its language, a project that isn't published): a warning
    /// that the new version isn't in it, which version the project holds, and how to put the new one there instead.
    /// </summary>
    /// <param name="intoVersion">The version the save goes into instead of a new one, when it does: still the project's, if it was.</param>
    /// <returns>Null when the content isn't in such a project, or the project holds the version saved into.</returns>
    /// <remarks>
    /// A failure to read the projects (the project repository's database, a site's own repository) only drops the warning,
    /// logged: a save that names no project never fails over projects.
    /// </remarks>
    public static ValidationIssue? NotInProject(CmsCall call, IContent writable, int? intoVersion)
    {
        if (ContentReference.IsNullOrEmpty(writable.ContentLink) || writable is not IVersionable)
        {
            return null;
        }
        try
        {
            return call.OptionalService<ProjectRepository>() is null ? null : NotInProjectWarning(call, writable, intoVersion);
        }
        catch (Exception ex) when (ex is not AgentException)
        {
            if (call.OptionalService<ILoggerFactory>() is { } loggers)
            {
                loggers.CreateLogger(CmsCall.LogCategory).LogWarning(ex, "Couldn't check whether {Content} is in a project; saved without the warning.", writable.ContentLink.ToString());
            }
            return null;
        }
    }

    private static ValidationIssue? NotInProjectWarning(CmsCall call, IContent writable, int? intoVersion)
    {
        var projects = new ProjectContext(call);
        var holding = projects.InOtherProjects(writable.ContentLink, ProjectContext.Language(writable), except: 0);
        if (holding.Count == 0 || (intoVersion is { } into && holding.Any(h => h.Item.ContentLink.WorkID == into)))
        {
            return null;
        }
        var (item, project) = holding[0];
        var id = project.ID.ToString(CultureInfo.InvariantCulture);
        // Only an active project takes the new version; a scheduled one is reactivated first.
        var next = project.Status switch
        {
            _ when ProjectRules.IsActive(project.Status) =>
                call.ForCaller($"To put it there instead, save with project {id} (--in-project {id}).", $"To put it there instead, save with project {id}."),
            ProjectStatus.DelayedPublished => call.ForCaller(
                $"The project is scheduled to be published: to put the new version there instead, reactivate it (opticli projects reactivate {id}) and save with project {id} (--in-project {id}).",
                $"The project is scheduled to be published: to put the new version there instead, it must be reactivated first (reactivate_project {id}, where the site lets assistants publish; else in the CMS), then saved with project {id}."),
            _ => "",
        };
        return new ValidationIssue(null,
            $"{What(writable)} is in project {ProjectLookup.Describe(project)}, which holds version {item.ContentLink}: the new version isn't in it. {next}".TrimEnd(),
            "warning");
    }

    /// <summary>The project's items of the content in <paramref name="language"/>.</summary>
    private List<ProjectItem> Mine(ContentReference content, CultureInfo language) =>
        _projects.Repository.GetItems([content.ToReferenceWithoutVersion()])
            .Where(i => i.ProjectID == _project.ID && ProjectContext.SameLanguage(i.Language, language))
            .ToList();

    private ProjectSaveResult Result(string item, string? replaced, bool ready) =>
        new(_project.ID, _project.Name) { Item = item, Replaced = replaced, Ready = ready, EditorUi = _editorUi };

    private ProjectSaveResult Failed(IContent saved, Exception exception, string item, string? replaced)
    {
        if (Call.OptionalService<ILoggerFactory>() is { } loggers)
        {
            loggers.CreateLogger(CmsCall.LogCategory).LogWarning(exception, "Saved {Content}, but the project step for project {Project} failed.", saved.ContentLink.ToString(), _project.ID);
        }
        return Result(item, replaced, ready: false) with
        {
            Error = exception is AgentException ? exception.Message : $"{exception.Message} ({exception.GetType().FullName})",
        };
    }

    private static string What(IContent content) => ContentReference.IsNullOrEmpty(content.ContentLink)
        ? $"New content '{content.Name}'"
        : ProjectContext.What(content);
}
