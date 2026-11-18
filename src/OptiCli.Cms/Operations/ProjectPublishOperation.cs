using EPiServer;
using EPiServer.Approvals;
using EPiServer.Approvals.ContentApprovals;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using Microsoft.Extensions.Logging;
using OptiCli.Cms.Content;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// Publishes, or schedules, a project (the agent's <c>POST /v1/projects/{project}/publish</c>): the items marked Ready to
/// publish, through the CMS's <c>ProjectPublisher</c>, as the edit UI's Publish does in project mode. Items that aren't
/// ready stay as they are, also where editors have the Projects gadget, whose Publish puts every unpublished item live.
/// </summary>
/// <remarks>
/// <para>Every ready item is checked first, as a single publish (<see cref="PublishOperation"/>) is: read, language and
/// (for an editor) Publish access, validation, served media, another person's draft it carries (<c>includeDraft</c>; not
/// for the drafts of whoever marked it ready, which they consented to by doing so), and
/// under an approval sequence a reviewer's approval of the version (CMS 12 lets the API check a version in past its
/// sequence, and a project publish would put that live). One item that fails stops the publish, with every failure
/// listed, so nothing goes live by halves over something opticli could tell up front; the edit UI instead leaves an item
/// the editor can't publish behind without a word.</para>
/// <para>The CMS publishes on a background task and the await returns once every item was tried and the project's final
/// status saved. An item it fails (or whose site handler throws after it went live) makes the project
/// <c>publishFailed</c>; the result says which items went live and which didn't. What the site's code did meanwhile is
/// recorded by <see cref="ProjectPublishEvents"/>.</para>
/// </remarks>
internal static class ProjectPublishOperation
{
    public static async Task<ProjectResult> RunAsync(CmsCall call, string reference, ProjectPublishRequest body)
    {
        var projects = new ProjectContext(call);
        var ui = projects.RequireEnabled();
        var project = projects.Resolve(reference);
        ProjectRules.RequireActive(call, project, "be published");
        if (body.PublishAt is { } at && at.ToUniversalTime() <= DateTime.UtcNow)
        {
            throw AgentException.Usage($"publishAt ({at.ToUniversalTime():u}) isn't in the future.", "Publish the project now instead, or give a later time.");
        }
        var scheduling = body.PublishAt is not null;
        var action = scheduling ? SaveAction.Schedule : SaveAction.Publish;
        // For an editor: the publishing gate, before anything else is looked at (a dry run's too).
        call.RequirePublishing(action);

        var flow = new WriteFlow(call);
        var all = projects.Items(project);
        var hiddenReady = all.Count(i => i.State == ProjectItemState.Ready && !call.CanRead(i.Version!));
        if (hiddenReady > 0)
        {
            throw AgentException.Refused(
                $"{hiddenReady} item(s) of project {ProjectLookup.Describe(project)} marked Ready to publish are content you can't read, so you can't publish the project.",
                "Ask someone with access to that content to publish the project, or to take those items out. Nothing was published.");
        }
        var visible = all.Where(i => i.Version is null || call.CanRead(i.Version)).ToList();
        var plans = visible.Select(item => item.State == ProjectItemState.Ready
            ? Check(call, flow, projects, item, action, body.IncludeDraft)
            : new ItemPlan(item, new ProjectItemResult { Content = projects.Summary(item), Outcome = ProjectRules.PublishOutcome(item.State, scheduling) })).ToList();
        var ready = plans.Where(p => p.Item.State == ProjectItemState.Ready).ToList();
        // Where the site's edit UI makes new blocks in an area shared: the drafts of the blocks of each item's own "For this
        // page" folder that it shows go live with it (WriteFlow.PublishAlong), but those that are the project's own items,
        // in any state: those follow the project's rules (live only when marked ready), and one left behind gets a warning.
        bool? InProject(int id, System.Globalization.CultureInfo? language)
        {
            var items = all.Where(i => i.Item.ContentLink.ID == id
                && (language is null || i.Item.Language is null || string.IsNullOrEmpty(i.Item.Language.Name) || string.Equals(i.Item.Language.Name, language.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            return items.Count == 0 ? null : items.Any(i => i.State == ProjectItemState.Ready);
        }
        ProjectItemResult Along(ProjectItemResult item, IContent version, bool dryRun)
        {
            var warnings = item.Validation?.ToList() ?? [];
            var along = flow.PublishAlong(version, action, body.IncludeDraft, dryRun, warnings, InProject, body.PublishAt?.ToLocalTime());
            return along is { Count: > 0 } || warnings.Count > (item.Validation?.Count ?? 0)
                ? item with { SharedBlocks = along is { Count: > 0 } ? along : null, Validation = warnings.Count > 0 ? warnings : null }
                : item;
        }
        if (ready.Count == 0)
        {
            throw new AgentException(AgentErrorCodes.Conflict,
                $"No item of project {ProjectLookup.Describe(project)} is marked Ready to publish, so publishing it would put nothing live.",
                call.ForCaller(
                    $"Mark the items ready first (opticli projects ready {project.ID} <ref>... or --all), then publish. Nothing was published.",
                    "Mark the items ready first (mark_ready_to_publish), when the user wants them to go live, then publish. Nothing was published."))
            {
                Reason = AgentErrorReasons.NothingReady,
            };
        }

        var result = Lists(new ProjectResult
        {
            Project = projects.Describe(project, all),
            EditorUi = ui,
            DryRun = body.DryRun,
            ScheduledFor = body.PublishAt?.ToUniversalTime(),
        }, plans.Select(p => p.Result).ToList());
        if (ready.Any(p => p.Result.Problem is not null))
        {
            return body.DryRun
                ? result with { Valid = false }
                : throw ProjectRules.Refusal("publish", ProjectLookup.Describe(project), result, ready.Select(p => p.Result).ToList(),
                    // Marking them ready again can't send them for review: they are ready already.
                    call.ForCaller(
                        "Those items were marked ready without their reviewers' approval: send each for review by saving it into the project with ready and requestApproval (--in-project, --ready, --request-approval), and publish the project once they are approved. Nothing was published.",
                        "Those items were marked ready without their reviewers' approval: send each for review by saving it into the project with ready and requestApproval, and publish the project once they are approved. Nothing was published."));
        }
        if (body.DryRun)
        {
            return Lists(result, plans.Select(p => p.Item.State == ProjectItemState.Ready && p.Item.Version is { } version ? Along(p.Result, version, dryRun: true) : p.Result).ToList());
        }

        projects.ThrowIfAborted();
        string? siteError = null;
        IReadOnlyList<SiteEvent> events = [];
        var items = ready.Select(p => p.Item.Item).ToList();
        var retried = 0;
        while (true)
        {
            using var recording = ProjectPublishEvents.Record(call.OptionalService<IContentEvents>(), project.ID, items.Select(i => i.ContentLink));
            try
            {
                await call.PublishProject((projects.Repository.Get(project.ID) ?? project).CreateWritableClone(), items, body.PublishAt?.ToLocalTime());
                events = recording?.Events ?? [];
                break;
            }
            catch (Exception ex) when (ex is not AgentException)
            {
                if (retried < MaxRetries && ExecutorRace(ex) && ready.All(p => projects.Load(p.Item.Item).State == ProjectItemState.Ready))
                {
                    // CMS 12's task executor lost its race before it published anything: the project is publishFailed
                    // with every item as it was, which publishes again.
                    retried++;
                    Log(call, ex, $"The CMS's background task failed to start the publish of project {project.ID}; trying again ({retried} of {MaxRetries}).");
                    continue;
                }
                // The CMS went on with the other items; which went live is read back below.
                siteError = Messages(ex);
                events = recording?.Events ?? [];
                Log(call, ex, $"The CMS failed part of the publish of project {project.ID}.");
                break;
            }
        }

        var after = projects.Items(project);
        var done = plans.Select(plan => plan.Item.State != ProjectItemState.Ready ? plan.Result : Outcome(projects, after, plan, scheduling, siteError))
            .Select(item => item.Outcome is ProjectItemOutcomes.Published or ProjectItemOutcomes.Scheduled
                && plans.FirstOrDefault(p => p.Item.State == ProjectItemState.Ready && p.Result.Content.Id == item.Content.Id && p.Result.Content.Language == item.Content.Language) is { } plan
                && flow.Repository.TryGet<IContent>(plan.Item.Item.ContentLink, out var version)
                    ? Along(item, version, dryRun: false)
                    : item)
            .ToList();
        var types = call.Service<IContentTypeRepository>();
        return Lists(result with
        {
            Project = projects.Describe(projects.Repository.Get(project.ID) ?? project, after),
            SiteError = siteError,
            Validation = retried == 0 ? null : [new ValidationIssue(null,
                $"The CMS's background task failed to start the publish {retried} time(s) before it published anything (CMS 12's task executor: \"Cannot access a disposed object\"), so opticli tried again.",
                "warning")],
            SiteChanges = SiteChanges.ReportProjectPublish(call, types, events, items.Select(i => i.ContentLink.ID).ToHashSet()),
        }, done);
    }

    private sealed record ItemPlan(LoadedItem Item, ProjectItemResult Result);

    /// <summary>The checks a single publish of the ready item's version makes, as a problem when one fails.</summary>
    private static ItemPlan Check(CmsCall call, WriteFlow flow, ProjectContext projects, LoadedItem item, SaveAction action, bool includeDraft)
    {
        var outcome = WriteFlow.Kind(action) == SaveAction.Schedule ? ProjectItemOutcomes.Scheduled : ProjectItemOutcomes.Published;
        var version = item.Version!;
        var what = ProjectContext.What(item);
        var shown = projects.Summary(item);
        var language = version is ILocalizable { Language: { } own } ? own : null;
        var branch = flow.Locator.Versions(version.ContentLink, language, Compat.CmsApi.Variation(version));
        var previouslyPublished = ContentLocator.PublishedVersion(branch);
        try
        {
            call.RequireLanguageAccess(version);
            call.RequireAccess(version, AccessLevel.Publish);
            // Before validation: CMS 13's own validator refuses an unapproved version too ("Content must be approved"),
            // which would make the same item a validation problem there and an approval one on CMS 12.
            if (Approvals.Applying(call, version.ContentLink) is not null && !Approved(call, version.ContentLink))
            {
                return Problem(new ProjectItemProblem(AgentErrorCodes.Refused,
                    $"{what} has an approval sequence, and its reviewers haven't approved version {version.ContentLink}: publishing it would skip them.",
                    AgentErrorReasons.ApprovalSequence));
            }
            var writable = (IContent)((IReadOnly)version).CreateWritableClone();
            var issues = ValidationErrors.Validate(call, writable, action);
            if (ValidationErrors.HasErrors(issues))
            {
                return Problem(new ProjectItemProblem(AgentErrorCodes.Validation, $"{what} doesn't pass validation."), issues);
            }
            if (!call.MayWriteScript && version is MediaData media)
            {
                UploadOperation.RequireServedSafely(call, media, null,
                    previouslyPublished is { } live && flow.Repository.TryGet<IContent>(new ContentReference(version.ContentLink.ID, live, version.ContentLink.ProviderName), out var published) ? published : null);
            }
            // Whoever marked the version ready consented to publishing it, their own earlier drafts in it included; another
            // person's draft it carries still needs includeDraft. The result shows whose changes go live either way.
            var stored = branch.FirstOrDefault(v => v.ContentLink.WorkID == version.ContentLink.WorkID);
            var readyBy = PendingDrafts.ReadyBy(stored?.Status == VersionStatus.CheckedIn, stored?.StatusChangedBy, call.UserName);
            var unconfirmed = flow.Locator.PendingDraft(branch, version, readyBy);
            var pending = unconfirmed ?? (readyBy is null ? null : flow.Locator.PendingDraft(branch, version));
            if (unconfirmed is not null && !includeDraft)
            {
                return Problem(new ProjectItemProblem(AgentErrorCodes.Conflict,
                    $"Publishing {what} would also put live {unconfirmed.Describe()}{(readyBy is null ? "" : $" ({readyBy} marked it ready, but those changes aren't theirs)")}.",
                    AgentErrorReasons.PendingDraft), pending: unconfirmed, readyBy: readyBy);
            }
            var warnings = issues.ToList();
            if (version is IVersionable { StopPublish: { } stop } && stop <= DateTime.Now)
            {
                warnings.Add(new ValidationIssue(PropertyWriter.StopPublishKey,
                    $"Its stop-publish date ({stop.ToUniversalTime():u}) has passed, so it goes live expired: visitors won't see it.", "warning"));
            }
            return new ItemPlan(item, new ProjectItemResult
            {
                Content = shown,
                Outcome = outcome,
                Validation = warnings.Count > 0 ? warnings : null,
                PendingDraft = pending,
                ReadyBy = readyBy,
                PreviouslyPublished = previouslyPublished,
            });
        }
        catch (AgentException ex)
        {
            return Problem(ProjectRules.Problem(ex, what), pending: ex.PendingDraft);
        }

        ItemPlan Problem(ProjectItemProblem problem, IReadOnlyList<ValidationIssue>? issues = null, PendingDraft? pending = null, string? readyBy = null) =>
            new(item, new ProjectItemResult { Content = shown, Outcome = outcome, Problem = problem, Validation = issues, PendingDraft = pending, ReadyBy = readyBy, PreviouslyPublished = previouslyPublished });
    }

    /// <summary>Whether a reviewer approved the version (its approval is <c>Approved</c>, as when the sequence ends).</summary>
    private static bool Approved(CmsCall call, ContentReference version) =>
        call.Service<IApprovalRepository>().GetAsync(version).GetAwaiter().GetResult() is { Status: ApprovalStatus.Approved };

    /// <summary>A ready item after the publish: published (scheduled), or failed with the CMS's message.</summary>
    private static ProjectItemResult Outcome(ProjectContext projects, IReadOnlyList<LoadedItem> after, ItemPlan plan, bool scheduling, string? siteError)
    {
        var now = after.FirstOrDefault(i => i.Item.ID == plan.Item.Item.ID) ?? projects.Load(plan.Item.Item);
        var went = scheduling ? ProjectItemState.Scheduled : ProjectItemState.Published;
        return now.State == went && (now.Version as IVersionable)?.Status is not VersionStatus.PreviouslyPublished
            ? plan.Result with { Content = projects.Summary(now) }
            : plan.Result with
            {
                Content = projects.Summary(now),
                Outcome = ProjectItemOutcomes.Failed,
                Error = siteError ?? "The CMS didn't publish it.",
                PreviouslyPublished = null,
            };
    }

    /// <summary><paramref name="result"/> with the items sorted into its lists by outcome.</summary>
    private static ProjectResult Lists(ProjectResult result, IReadOnlyList<ProjectItemResult> items)
    {
        IReadOnlyList<ProjectItemResult>? Of(string outcome) => items.Where(i => i.Outcome == outcome).ToList() is { Count: > 0 } list ? list : null;
        return result with
        {
            Published = Of(ProjectItemOutcomes.Published),
            Scheduled = Of(ProjectItemOutcomes.Scheduled),
            Failed = Of(ProjectItemOutcomes.Failed),
            NotReady = Of(ProjectItemOutcomes.NotReady),
            AlreadyPublished = Of(ProjectItemOutcomes.AlreadyPublished),
        };
    }

    /// <summary>How many times a publish the CMS's task executor failed to start is tried again (<see cref="ExecutorRace"/>).</summary>
    private const int MaxRetries = 2;

    /// <summary>
    /// The CMS 12 task executor's race: <c>DefaultTaskExecutor.Start</c> makes a service scope, starts the task in the
    /// caller's execution context (which then holds that scope), and disposes the scope as it returns; a task that starts
    /// a moment later makes its own scope from the disposed one, and fails with <see cref="ObjectDisposedException"/>
    /// ("IServiceProvider") before it publishes anything. CMS 13 starts the task without the caller's context.
    /// </summary>
    private static bool ExecutorRace(Exception exception)
    {
        IReadOnlyList<Exception> inner = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
        return inner.Count > 0 && inner.All(e => e is ObjectDisposedException);
    }

    private static void Log(CmsCall call, Exception exception, string message)
    {
        if (call.OptionalService<ILoggerFactory>() is { } loggers)
        {
            loggers.CreateLogger(CmsCall.LogCategory).LogWarning(exception, "{Message}", message);
        }
    }

    /// <summary>The CMS's failure, its inner exceptions' messages (the access refusal of an item, a handler's error), not "One or more errors occurred."</summary>
    private static string Messages(Exception exception)
    {
        IReadOnlyList<Exception> inner = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
        return string.Join(" ", inner.Select(e => $"{e.Message} ({e.GetType().FullName})").Distinct());
    }
}
