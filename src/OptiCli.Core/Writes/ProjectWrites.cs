using System.Globalization;
using System.Text;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// <c>opticli projects create|ready|remove|publish|reactivate|delete</c>: the site agent's project routes, with the drift
/// check of every write, refs resolved against the database, and errors in command-line terms.
/// </summary>
/// <remarks>
/// A project is named by its id or its exact name, which the site resolves (a name more than one unpublished project has is
/// a <c>usage</c> error). Publishing a project publishes only its items marked Ready to publish, in every edit UI mode: the
/// Projects gadget's own Publish (CMS 13's default) puts every unpublished item live instead.
/// </remarks>
public sealed class ProjectWrites(WriteExecutor executor)
{
    /// <summary>What to do about a project publish that would put other people's unpublished changes live.</summary>
    public const string PendingDraftHint =
        "details.project lists each ready item, and pendingDraft on an item the unpublished changes someone else saved that it would put live (not those of whoever marked it ready, readyBy, which need no confirming). Ask the user whether those should go live too, and only if so run again with --include-draft. To leave them out, save the item again into the project from its published version (opticli set <ref> ... --from published --in-project <project> --ready). Nothing was published.";

    /// <summary>A refusal over several items with different problems.</summary>
    public const string ProjectItemsHint =
        "details.project lists every item, each one that stops it with problem (code, message). Fix those items, or take them out of the project (opticli projects remove), then run again. Nothing was changed.";

    /// <summary><c>projects ready</c> of an item whose version someone else saved, without <c>--include-draft</c>.</summary>
    public const string ReadyPendingDraftHint =
        "details.project lists each item, and pendingDraft on an item the draft someone else saved. Marking it ready records opticli as the one who saved it, so the project's publish wouldn't ask about those changes. Ask the user whether they should go live; only if so run again with --include-draft. Nothing was changed.";

    /// <summary><c>projects ready</c> under an approval sequence, without <c>--request-approval</c>.</summary>
    public const string ReadyApprovalHint =
        "Ask the user whether to send those items for review instead; if so, run again with --request-approval (and --comment \"...\" for the reviewers, required where the sequence says so). Reviewers then approve them in the CMS, after which they count as ready. Nothing was changed.";

    /// <exception cref="OptiCliException">The site's refusal; for a name an unpublished project has, a <c>conflict</c>.</exception>
    public Task<ProjectResult> CreateAsync(string name, bool dryRun, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, AgentRoutes.Projects, new ProjectCreateRequest { Name = name, DryRun = dryRun }, dryRun, cancellationToken);

    /// <param name="items">Refs of the items (ids, versions, GUIDs, URLs); null with <paramref name="all"/>.</param>
    /// <param name="includeDraft">Also mark ready items whose version someone else saved.</param>
    public async Task<ProjectResult> ReadyAsync(string project, IReadOnlyList<string>? items, string? lang, bool all, bool requestApproval, string? comment, bool includeDraft,
        bool dryRun, CancellationToken cancellationToken)
    {
        if ((items is { Count: > 0 }) == all)
        {
            throw new UsageException(all ? "Give the items to mark ready, or --all, not both." : "Give the items to mark ready (refs), or --all for every draft in the project.",
                "Example: opticli projects ready 12 123 124, or opticli projects ready 12 --all.");
        }
        if (comment is not null && !requestApproval)
        {
            throw new UsageException("--comment goes to the reviewers of a review request: give --request-approval too, or leave the comment out.");
        }
        var request = new ProjectReadyRequest
        {
            Items = all ? null : await RefsAsync(items!, cancellationToken),
            Lang = Language(lang),
            All = all,
            RequestApproval = requestApproval,
            Comment = comment,
            IncludeDraft = includeDraft,
            DryRun = dryRun,
        };
        return await SendAsync(HttpMethod.Post, AgentRoutes.ProjectReady(project), request, dryRun, cancellationToken);
    }

    public async Task<ProjectResult> RemoveAsync(string project, IReadOnlyList<string> items, string? lang, bool dryRun, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            throw new UsageException("Give the items to take out of the project (refs).", "Example: opticli projects remove 12 123. Their versions stay as they are.");
        }
        var request = new ProjectRemoveRequest { Items = await RefsAsync(items, cancellationToken), Lang = Language(lang), DryRun = dryRun };
        return await SendAsync(HttpMethod.Post, AgentRoutes.ProjectRemove(project), request, dryRun, cancellationToken);
    }

    /// <summary>
    /// Publishes (or schedules, <paramref name="at"/>) the project's items marked Ready to publish. With
    /// <paramref name="confirm"/> (a person at a terminal), a dry run first shows what goes live, what stays behind and
    /// whose unpublished changes go with it, and the publish runs only on a yes (confirming those changes too). Without
    /// it, other people's changes need <paramref name="includeDraft"/>.
    /// </summary>
    /// <param name="confirm">Asks the summary's question (the summary, then the question); null: no one to ask.</param>
    /// <exception cref="ConflictException">Declined at the prompt; other people's changes without <paramref name="includeDraft"/>; nothing ready.</exception>
    public async Task<ProjectResult> PublishAsync(string project, DateTimeOffset? at, bool includeDraft, bool dryRun, Func<string, string, bool>? confirm,
        CancellationToken cancellationToken)
    {
        var route = AgentRoutes.ProjectPublish(project);
        var request = new ProjectPublishRequest { PublishAt = at?.UtcDateTime, IncludeDraft = includeDraft, DryRun = dryRun };
        if (dryRun || confirm is null)
        {
            return await SendAsync(HttpMethod.Post, route, request, dryRun, cancellationToken);
        }
        // What the publish would do, other people's changes included, so the person sees them before saying yes.
        var preview = await SendAsync(HttpMethod.Post, route, request with { DryRun = true, IncludeDraft = true }, dryRun: true, cancellationToken);
        if (!preview.Valid)
        {
            // An item that can't go: the publish itself refuses, with every item's problem, before anything goes live.
            return await SendAsync(HttpMethod.Post, route, request, dryRun: false, cancellationToken);
        }
        var carried = Live(preview).Count(i => i.PendingDraft is not null);
        var withDrafts = carried > 0 ? $", with the other people's changes {(carried == 1 ? "it carries" : "they carry")}" : "";
        var question = at is null
            ? $"Publish {Count(Live(preview).Count, "item")} now{withDrafts}?"
            : $"Schedule {Count(Live(preview).Count, "item")}{withDrafts}?";
        if (!confirm(Summary(preview), question))
        {
            throw new ConflictException($"Not {(at is null ? "published" : "scheduled")}, as answered; nothing was published.",
                "The project stays as it is; its items are still marked ready.");
        }
        return await SendAsync(HttpMethod.Post, route, request with { IncludeDraft = includeDraft || carried > 0 }, dryRun: false, cancellationToken);
    }

    public Task<ProjectResult> ReactivateAsync(string project, bool dryRun, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, AgentRoutes.ProjectReactivate(project), new ProjectReactivateRequest { DryRun = dryRun }, dryRun, cancellationToken);

    /// <summary>
    /// Deletes the project; its items' versions stay as they are. The site has no dry run for it, so a dry run checks the
    /// project's status in the database (<see cref="ProjectDeletePreview"/>).
    /// </summary>
    public async Task<object> DeleteAsync(string project, bool dryRun, CancellationToken cancellationToken)
    {
        if (!dryRun)
        {
            return await SendAsync(HttpMethod.Delete, AgentRoutes.ProjectDelete(project), null, dryRun: false, cancellationToken);
        }
        var session = executor.Session;
        var found = ProjectReader.Require(await ProjectReader.ListAsync(session.Db, cancellationToken), project);
        (string? Message, string? Hint) why = found.Status switch
        {
            ProjectStatuses.DelayedPublished => ("is scheduled to be published, so deleting it would leave its items scheduled without a project to reactivate",
                $"Reactivate it first (opticli projects reactivate {found.Id}), which cancels the scheduled publish, then delete it."),
            ProjectStatuses.Publishing => ("is being published right now", "Try again once the publish is done."),
            ProjectStatuses.Reactivating => ("is being reactivated right now", "Try again once that is done."),
            _ => (null, null),
        };
        if (why.Message is not null)
        {
            throw new ConflictException($"Project {ProjectReader.Describe(found)} {why.Message}.", $"{why.Hint} Nothing was changed.")
            {
                Details = new AgentErrorDetails(null, null) { Reason = AgentErrorReasons.ProjectNotActive },
            };
        }
        return new ProjectDeletePreview(Summary(found), DryRun: true, Deleted: false, await ProjectReader.ItemsAsync(session, found.Id, cancellationToken));
    }

    /// <summary>
    /// For the prompt before a project publish: what goes live (and the unpublished changes of others it carries), and what
    /// stays as it is.
    /// </summary>
    public static string Summary(ProjectResult preview)
    {
        var text = new StringBuilder();
        var project = preview.Project;
        var live = Live(preview);
        var at = preview.ScheduledFor is { } scheduled ? $" at {scheduled.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)}" : "";
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Project {project.Id} ('{project.Name}', {project.Status}): {(preview.ScheduledFor is null ? "publishing puts" : $"scheduling puts")} {Count(live.Count, "item")} live{at}, the ones marked Ready to publish:");
        foreach (var item in live)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {Describe(item.Content)}");
            if (item.PendingDraft is { } draft)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"      also puts live {draft.Describe()}");
            }
            if (item.ReadyBy is { } readyBy)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"      marked ready by {readyBy}");
            }
        }
        if (preview.NotReady is { Count: > 0 } notReady)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Stays behind, not ready ({notReady.Count}):");
            foreach (var item in notReady)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {Describe(item.Content)}");
            }
        }
        if (preview.AlreadyPublished is { Count: > 0 } published)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Published already, left as it is ({published.Count}):");
            foreach (var item in published)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {Describe(item.Content)}");
            }
        }
        foreach (var warning in preview.Validation ?? [])
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Note: {warning.Message}");
        }
        foreach (var item in live.Where(i => i.Validation is { Count: > 0 }))
        {
            foreach (var warning in item.Validation!)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Note on {item.Content.Ref}: {warning.Message}");
            }
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>A project as the agent summarizes one, from the database (<see cref="ProjectReader"/>).</summary>
    public static ProjectSummary Summary(ProjectInfo project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Status = project.Status,
        ScheduledFor = project.Status == ProjectStatuses.DelayedPublished ? project.PublishAt : null,
        Created = project.Created,
        CreatedBy = project.CreatedBy,
        Items = project.Items,
        Ready = project.Ready,
    };

    private static IReadOnlyList<ProjectItemResult> Live(ProjectResult result) => result.Published ?? result.Scheduled ?? [];

    private static string Describe(ContentSummary content) =>
        $"{content.Ref} '{content.Name}'{(content.Type is null ? "" : $" ({content.Type})")}{(content.Language is null ? "" : $", {content.Language}")}{(content.Status is null ? "" : $", {content.Status}")}";

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    /// <summary>The refs as the agent takes them: <c>123</c> for content, <c>123_456</c> for a version.</summary>
    private async Task<IReadOnlyList<string>> RefsAsync(IReadOnlyList<string> refs, CancellationToken cancellationToken)
    {
        var resolved = new List<string>();
        foreach (var reference in refs)
        {
            var located = await executor.Session.LocateAsync(reference, null, cancellationToken);
            resolved.Add(located.VersionId is { } version ? WriteOutput.VersionRef(located.Id, version) : WriteOutput.Id(located.Id));
        }
        return resolved;
    }

    private string? Language(string? code) => code is null ? null : executor.Session.Language(code)?.Code ?? code;

    private async Task<ProjectResult> SendAsync(HttpMethod method, string route, object? body, bool dryRun, CancellationToken cancellationToken)
    {
        try
        {
            return await executor.ProjectAsync(method, route, body, dryRun, cancellationToken);
        }
        catch (OptiCliException ex) when (Translate(ex, route) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>The agent's errors about project items, with hints that name the options instead of request fields.</summary>
    internal static OptiCliException? Translate(OptiCliException ex, string route)
    {
        if (ex.Details is not AgentErrorDetails details)
        {
            return null;
        }
        var ready = route.EndsWith("/ready", StringComparison.Ordinal);
        return details.Reason switch
        {
            AgentErrorReasons.PendingDraft => new ConflictException(ex.Message, ready ? ReadyPendingDraftHint : PendingDraftHint) { Details = details },
            AgentErrorReasons.ApprovalSequence when ready => new RefusedException(ex.Message, ReadyApprovalHint) { Details = details },
            AgentErrorReasons.ProjectItems => OptiCliException.Create(ex.Code, ex.Message, ProjectItemsHint, details),
            AgentErrorReasons.InReview => new ConflictException(ex.Message, ApprovalRules.InReviewHint) { Details = details },
            AgentErrorReasons.NoApprovalSequence => new UsageException(ex.Message,
                "Without --request-approval the items are marked ready; it only applies where an approval sequence does.") { Details = details },
            _ => null,
        };
    }
}

/// <summary>A dry run of <c>projects delete</c>, checked in the database: the project and the items it would take with it (their versions stay).</summary>
public sealed record ProjectDeletePreview(ProjectSummary Project, bool DryRun, bool Deleted, IReadOnlyList<ProjectItemInfo> Items);
