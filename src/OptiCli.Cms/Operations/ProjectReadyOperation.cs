using EPiServer.Approvals;
using EPiServer.Approvals.ContentApprovals;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAccess;
using EPiServer.Security;
using OptiCli.Cms.Content;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Marks a project's items Ready to publish (the agent's <c>POST /v1/projects/{project}/ready</c>), as the edit UI's
/// "Ready to Publish" does: a <c>SaveAction.CheckIn</c> of each item's version, which keeps its version id and needs Edit
/// access. Where an approval sequence applies, <c>requestApproval</c> sends the item for review instead, with the
/// comment, as "Ready for Review" does.
/// </summary>
/// <remarks>
/// <para>Every item is checked before any is changed (state, language access, Edit access, the sequence, validation,
/// another person's draft), so a refusal changes nothing. What the CMS still refuses while committing is reported per
/// item, and the others go ahead, as the edit UI lists each item it couldn't mark. With <c>all</c>, drafts the caller may
/// not edit are left out and listed (<see cref="ProjectItemOutcomes.NoAccess"/>) instead of stopping the request.</para>
/// <para>The CMS records whoever checks a version in as the one who saved it (<see cref="PendingDrafts"/>), so marking
/// another person's draft ready would hide their changes from the publish's pending-draft check: it needs
/// <c>includeDraft</c>, as publishing their draft does.</para>
/// </remarks>
internal static class ProjectReadyOperation
{
    public static ProjectResult Run(CmsCall call, string reference, ProjectReadyRequest body)
    {
        var refs = body.Items?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? [];
        if (body.All == refs.Count > 0)
        {
            throw AgentException.Usage(body.All ? "Give items or all, not both." : "Give the items to mark ready, or all.");
        }
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        if (comment is not null && !body.RequestApproval)
        {
            throw AgentException.Usage("A comment goes with a review request: give requestApproval too, or leave the comment out.");
        }
        var projects = new ProjectContext(call);
        var ui = projects.RequireEnabled();
        var project = projects.Resolve(reference);
        ProjectRules.RequireActive(call, project, "have items marked ready");

        var flow = new WriteFlow(call);
        var language = body.Lang is null ? null : flow.Locator.EnabledLanguage(body.Lang);
        var all = projects.Items(project);
        var selected = body.All
            ? all.Where(i => i.Version is { } version && call.CanRead(version) && (language is null || ProjectContext.SameLanguage(i.Item.Language, language))).ToList()
            // An item of content the editor can't read is, for them, not in the project (its name stays out of the result).
            : ProjectSelection.Select(all.Where(projects.Visible).ToList(), refs.Select(r => (r, flow.Locator.Resolve(r))).ToList(), language, ProjectLookup.Describe(project));

        var plans = selected.Select(item => Plan(call, flow, projects, item, body.All, body.RequestApproval, comment, body.IncludeDraft)).ToList();
        var result = new ProjectResult
        {
            Project = projects.Describe(project, all),
            EditorUi = ui,
            DryRun = body.DryRun,
            Items = plans.Select(p => p.Result).ToList(),
        };
        if (plans.Any(p => p.Result.Problem is not null))
        {
            return body.DryRun
                ? result with { Valid = false }
                : throw ProjectRules.Refusal("mark items ready in", ProjectLookup.Describe(project), result, result.Items!);
        }
        if (body.DryRun || plans.All(p => p.Action is null))
        {
            return result;
        }

        projects.ThrowIfAborted();
        var done = plans.Select(plan => plan.Action is { } action ? Commit(call, plan, action, comment) : plan.Result).ToList();
        var after = projects.Items(project);
        return result with { Project = projects.Describe(project, after), Items = done };
    }

    /// <param name="Action">How the item is committed; null when it is left as it is (ready already, skipped, or a problem).</param>
    private sealed record ItemPlan(LoadedItem Item, SaveAction? Action, ProjectItemResult Result);

    /// <summary>What marking <paramref name="item"/> ready takes, checked; a problem when it can't be.</summary>
    private static ItemPlan Plan(CmsCall call, WriteFlow flow, ProjectContext projects, LoadedItem item, bool all, bool requestApproval, string? comment, bool includeDraft)
    {
        var what = ProjectContext.What(item);
        var shown = projects.Summary(item);
        ItemPlan Leave(string outcome, IReadOnlyList<ValidationIssue>? issues = null) => new(item, null, new ProjectItemResult { Content = shown, Outcome = outcome, Validation = issues });
        ItemPlan Problem(ProjectItemProblem problem, IReadOnlyList<ValidationIssue>? issues = null, PendingDraft? pending = null) =>
            new(item, null, new ProjectItemResult { Content = shown, Problem = problem, Validation = issues, PendingDraft = pending });

        if (item.State == ProjectItemState.Ready)
        {
            return Leave(ProjectItemOutcomes.AlreadyReady);
        }
        if (item.State != ProjectItemState.Draft)
        {
            return all ? Leave(ProjectItemOutcomes.Skipped) : Problem(ProjectRules.CantMarkReady(item.State, what)!);
        }

        var version = item.Version!;
        try
        {
            try
            {
                call.RequireLanguageAccess(version);
                call.RequireAccess(version, AccessLevel.Edit);
            }
            catch (AgentException ex) when (all)
            {
                // Every draft, as far as the caller may edit: the others are listed, not a reason to stop.
                return Leave(ProjectItemOutcomes.NoAccess, [new ValidationIssue(null, $"Left as it is: {ex.Message}", "warning")]);
            }
            var sequence = Approvals.Applying(call, version.ContentLink);
            if (sequence is not null && !requestApproval)
            {
                return Problem(new ProjectItemProblem(AgentErrorCodes.Refused,
                    $"{what} has an approval sequence, so it isn't marked Ready to publish: that would let it past its reviewers.", AgentErrorReasons.ApprovalSequence));
            }
            if (sequence is { RequireCommentOnStart: true } && comment is null)
            {
                return Problem(new ProjectItemProblem(AgentErrorCodes.Usage, $"{what}: its approval sequence asks for a comment with each review request."));
            }
            var action = sequence is null ? SaveAction.CheckIn : SaveAction.RequestApproval;
            var issues = ValidationErrors.Validate(call, Writable(version), action);
            if (ValidationErrors.HasErrors(issues))
            {
                return Problem(new ProjectItemProblem(AgentErrorCodes.Validation, $"{what} doesn't pass validation."), issues);
            }
            var language = version is ILocalizable { Language: { } own } ? own : null;
            var branch = flow.Locator.Versions(version.ContentLink, language, Compat.CmsApi.Variation(version));
            var theirs = TheirDraft(flow, branch, version);
            if (theirs is not null && !includeDraft)
            {
                return Problem(new ProjectItemProblem(AgentErrorCodes.Conflict,
                    $"Version {version.ContentLink} of {what} is a draft of {(string.IsNullOrWhiteSpace(theirs.SavedBy) ? "nobody signed in (a scheduled job or import)" : theirs.SavedBy)}: marking it {(action == SaveAction.CheckIn ? "ready" : "for review")} would record you as the one who saved it, so publishing the project would put {theirs.Describe()} live without asking.",
                    AgentErrorReasons.PendingDraft), pending: theirs);
            }
            var warnings = issues.Concat(NewerVersions(branch, version)).ToList();
            return new(item, action, new ProjectItemResult
            {
                Content = shown,
                Outcome = action == SaveAction.CheckIn ? ProjectItemOutcomes.Ready : ProjectItemOutcomes.ApprovalRequested,
                Validation = warnings.Count > 0 ? warnings : null,
                PendingDraft = theirs,
            });
        }
        catch (AgentException ex)
        {
            return Problem(ProjectRules.Problem(ex, what));
        }
    }

    /// <summary>
    /// A warning when the item's version isn't the newest of its branch: marked ready, it becomes the primary draft (the
    /// one the CMS edit UI opens), and the newer versions stay as they are.
    /// </summary>
    private static IEnumerable<ValidationIssue> NewerVersions(IReadOnlyList<EPiServer.DataAbstraction.ContentVersion> branch, IContent version)
    {
        var newer = branch
            .Where(v => v.ContentLink.WorkID > version.ContentLink.WorkID)
            .Select(v => v.ContentLink.ToString())
            .ToList();
        if (newer.Count > 0)
        {
            yield return new ValidationIssue(null,
                $"Version {version.ContentLink} isn't the newest of its branch ({string.Join(", ", newer)} {(newer.Count == 1 ? "is" : "are")} newer): marked ready, it becomes the primary draft, the one the CMS edit UI opens; the newer versions stay as they are and aren't in the project.",
                "warning");
        }
    }

    /// <summary>
    /// The item's version itself, when someone other than the caller saved it and it differs from the published version:
    /// checking it in would record the caller as its saver. Another person's older draft it carries keeps their name, so the
    /// publish still asks about that one.
    /// </summary>
    private static PendingDraft? TheirDraft(WriteFlow flow, IReadOnlyList<EPiServer.DataAbstraction.ContentVersion> branch, IContent version) =>
        flow.Locator.PendingDraft(branch, version) is { } pending
        && pending.Version == $"{version.ContentLink.ID.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{version.ContentLink.WorkID.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            ? pending
            : null;

    /// <summary>Commits one item; what the CMS refuses is that item's failure, and the others go ahead.</summary>
    private static ProjectItemResult Commit(CmsCall call, ItemPlan plan, SaveAction action, string? comment)
    {
        var version = plan.Item.Version!;
        try
        {
            call.Save(Writable(version), action);
        }
        catch (Exception ex)
        {
            return plan.Result with { Outcome = ProjectItemOutcomes.Failed, Error = ex is AgentException ? ex.Message : $"{ex.Message} ({ex.GetType().FullName})" };
        }
        var warnings = plan.Result.Validation?.ToList() ?? [];
        if (action == SaveAction.RequestApproval && comment is not null && Comment(call, version.ContentLink, comment) is { } failed)
        {
            warnings.Add(failed);
        }
        // A read racing the commit can leave CMS 12's cache with the version as it was (ProjectContext.Load).
        call.OptionalService<EPiServer.IContentCacheRemover>()?.RemoveVersion(version.ContentLink);
        var stored = call.Service<EPiServer.IContentRepository>().Get<IContent>(version.ContentLink);
        return plan.Result with
        {
            Content = ContentSummaries.Describe(stored, call.Service<EPiServer.DataAbstraction.IContentTypeRepository>()),
            Validation = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>Adds the comment to the review the request just started, as the edit UI does; a warning when it couldn't.</summary>
    private static ValidationIssue? Comment(CmsCall call, ContentReference version, string comment)
    {
        try
        {
            var approval = call.Service<IApprovalRepository>().GetAsync(version).GetAwaiter().GetResult();
            if (approval is null)
            {
                return new ValidationIssue(null, $"The review of {version} was started, but there was no review to add the comment to.", "warning");
            }
            call.Service<IApprovalEngine>().CommentAsync(approval.ID, call.UserName, -1, comment).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex) when (ex is not AgentException)
        {
            return new ValidationIssue(null, $"The review of {version} was started, but the comment couldn't be added: {ex.Message}", "warning");
        }
    }

    private static IContent Writable(IContent version) => (IContent)((IReadOnly)version).CreateWritableClone();
}
