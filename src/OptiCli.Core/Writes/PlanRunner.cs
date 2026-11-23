using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;

namespace OptiCli.Core.Writes;

public static class PlanStepStatus
{
    /// <summary>Checked by a dry run (the agent's, or opticli's for publish/move/delete).</summary>
    public const string Valid = "valid";

    /// <summary>Refers to content an earlier step creates, so only its shape and names could be checked.</summary>
    public const string Deferred = "deferred";

    /// <summary>
    /// Refers to content an earlier step creates, or to content earlier steps change first, and was dry-run as that
    /// content will be (see <see cref="PlanSimulation"/>); its warnings say against what, and what could not be checked yet.
    /// </summary>
    public const string Simulated = "simulated";

    public const string Invalid = "invalid";
    public const string Saved = "saved";

    /// <summary>Ran, but found everything as the step wants it (<c>apply --update-existing</c>, or a set that changes nothing).</summary>
    public const string Unchanged = "unchanged";
    public const string Failed = "failed";
    public const string NotRun = "notRun";
}

/// <param name="Result">The step's write result (as the single command would print it).</param>
/// <param name="Undo">How to reverse the step, for saved steps.</param>
/// <param name="Guid">The GUID a create, block or upload step gives its content, when the plan fixes one.</param>
public sealed record PlanStepResult(
    int Index,
    string Op,
    string? Id,
    [property: Protocol.OutputValues(typeof(PlanStepStatus))] string Status,
    object? Result = null,
    ErrorBody? Error = null,
    string? Undo = null,
    IReadOnlyList<string>? Warnings = null,
    Guid? Guid = null)
{
    /// <summary>The draft an earlier operation made that this one saved into, instead of a new version (<see cref="PlanDrafts"/>).</summary>
    public SavedInto? SavedInto { get; init; }
}

/// <param name="Created">Plan id to content ref, for every created item.</param>
public sealed record PlanRun(bool DryRun, IReadOnlyList<PlanStepResult> Operations, IReadOnlyDictionary<string, string> Created)
{
    /// <summary>True when the plan stopped before its last operation; <see cref="Operations"/> says which were saved.</summary>
    public bool? Partial { get; init; }

    /// <summary>The projects the plan's steps save into (<c>project</c>): which exist, which the run made (or would make).</summary>
    public IReadOnlyList<PlanProject>? Projects { get; init; }
}

/// <summary>
/// Runs a <see cref="WritePlan"/>: validates every step first (nothing is written if any fails), then
/// executes them in order, stopping at the first failure with what was saved and how to undo it.
/// </summary>
/// <param name="planDirectory">The plan file's folder, which files in the plan are relative to (the working directory for stdin).</param>
/// <param name="allowOutside">Allow files outside <paramref name="planDirectory"/> (see <see cref="PlanFiles"/>).</param>
/// <param name="allowedTypes">Checks ContentArea placements of planned content against <c>[AllowedTypes]</c> in the code; null skips that.</param>
/// <param name="requestApproval">Every step that publishes requests approval where a sequence applies (<c>apply --request-approval</c>).</param>
public sealed class PlanRunner(ContentSession session, WriteExecutor executor, string planDirectory, bool allowOutside = false, Queries.AllowedTypesCheck? allowedTypes = null, bool requestApproval = false)
{
    /// <summary>
    /// <c>sync pull</c> only: a create, block or upload step that updates existing content which an earlier <c>move</c>
    /// step of the plan moves (by its GUID) is dry-run where the content is now, so it isn't taken for content that was
    /// moved elsewhere. Plain <c>apply</c> keeps its rule: existing content under another parent than the step's is a
    /// conflict.
    /// </summary>
    public bool MovesFirst { get; init; }

    /// <summary>The approval sequence of existing content steps publish below, by ref, read once per plan run.</summary>
    private readonly Dictionary<string, Queries.ApprovalSequence?> _approvals = new(StringComparer.Ordinal);

    /// <summary>Called after each step is checked (<c>"checking"</c>) and after each one runs (<c>"applying"</c>), with how many of how many.</summary>
    public Action<string, int, int>? Progress { get; init; }

    /// <summary>
    /// The plan's files as they were read once (a sealed apply): <c>"@path"</c> values come from there, never from the disk,
    /// so the files that run are the ones that were hashed.
    /// </summary>
    public Seals.PlanSnapshot? Files { get; init; }

    /// <summary>
    /// Called once the whole plan passed validation, right before the first step saves anything (a sealed apply checks
    /// production again there); an exception stops the plan with nothing saved.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeSaving { get; init; }

    /// <summary>
    /// Save every set, area and composition operation as a new version (<c>apply --version-per-step</c>), instead of into
    /// the draft the run already saved for that content and language (<see cref="PlanDrafts"/>).
    /// </summary>
    public bool VersionPerStep { get; init; }

    /// <exception cref="OptiCliException">
    /// Validation failed (nothing written), or a step failed during execution; <c>details</c> is the
    /// <see cref="PlanRun"/> with each step's status, results of the saved steps and undo hints.
    /// </exception>
    public async Task<PlanRun> RunAsync(WritePlan plan, bool dryRun, bool publishAll, CancellationToken cancellationToken)
    {
        if (publishAll && plan.Steps.Any(s => s.Operation.Project is not null))
        {
            throw new UsageException(
                "--publish publishes every operation, and a save into a project isn't published by itself: the project's publish puts its ready items live together.",
                "Leave out --publish, and publish the project once the plan ran (opticli projects publish <project>); or leave the project out of the plan. Nothing was changed.");
        }
        // The projects the plan names: by id, or by name, made when the plan runs if no unpublished project has the name.
        var projects = await PlanProjects.ResolveAsync(session, plan.Steps, cancellationToken);
        projects.RequireOneProjectPerContent(plan.Steps);
        // Rich text from @file values only has its $ids once the files are read.
        var planned = WritePlan.LinkText(PlanFiles.Resolve(plan.Steps
            .Select(s => publishAll ? s with { Operation = s.Operation.WithPublish() } : s)
            .Select(s => requestApproval ? s with { Operation = s.Operation.WithRequestApproval() } : s), planDirectory, allowOutside, Files));
        // Checked with the project's id; a project to be made doesn't exist yet, so those steps are checked without it.
        var steps = planned.Select(s => s with { Operation = projects.ForCheck(s.Operation) }).ToList();
        var guids = WritePlan.FixedGuids(steps);

        var existing = executor.UpdateExisting ? await ExistingAsync(steps, cancellationToken) : new Dictionary<string, int>();
        if (session.Model.Schema.Major >= 13)
        {
            WritePlan.RequireNoForwardLinks(steps, existing);
        }
        _existingCatalog = executor.UpdateExisting ? await ExistingCatalogAsync(steps, existing, cancellationToken) : new Dictionary<string, string>();
        var resolved = steps.Select(s => s with { Operation = WritePlan.Resolve(s, existing, guids, _existingCatalog) }).ToList();
        var targets = await TargetsAsync(resolved, existing, cancellationToken);
        var masters = await PlannedMastersAsync(resolved, existing, cancellationToken);
        var checks = new List<PlanStepResult>();
        OptiCliException? firstFailure = null;
        // Whether the site makes new area blocks inline, which an area add's "id" can't name (asked only where one has an id).
        var blockMode = steps.Any(s => PlanBlocks.Names(s.Operation)) ? await executor.InlineBlocksAsync(cancellationToken) : null;
        foreach (var step in steps)
        {
            var (result, failure) = await ValidateAsync(step, steps, existing, resolved, targets, masters, checks, cancellationToken);
            if (failure is null && blockMode == Protocol.InlineBlockModes.Inline && PlanBlocks.Names(step.Operation))
            {
                (result, failure) = NamedInline(step, steps, result);
            }
            else if (failure is null && blockMode == Protocol.InlineBlockModes.Shared && PlanBlocks.Names(step.Operation)
                && PlanBlocks.FoundInline((AreaEdit)step.Operation, result.Result as WriteOutput))
            {
                (result, failure) = FoundInline(step, steps, result);
            }
            var asPlanned = planned.First(p => p.Index == step.Index);
            if (failure is null && asPlanned.Operation.Ready && projects.ToMake(asPlanned.Operation))
            {
                // Checked without its project, so without ready: an approval sequence still refuses ready, as the run would.
                try
                {
                    await CheckPlannedApprovalAsync(asPlanned, steps, cancellationToken);
                }
                catch (OptiCliException ex)
                {
                    (result, failure) = (new PlanStepResult(step.Index, step.Operation.Kind, step.Operation.Id, PlanStepStatus.Invalid, ex.Details, Error(ex), Guid: step.Operation.ContentGuid), ex);
                }
            }
            checks.Add(projects.Note(asPlanned.Operation, result));
            firstFailure ??= failure;
            Progress?.Invoke("checking", checks.Count, steps.Count);
        }
        if (firstFailure is not null)
        {
            var invalid = checks.Where(c => c.Status == PlanStepStatus.Invalid).Select(c => c.Index).ToList();
            throw OptiCliException.Create(
                firstFailure.Code,
                $"Plan validation failed for operation(s) {string.Join(", ", invalid)}; nothing was changed. First problem: {firstFailure.Message}",
                firstFailure.Hint,
                new PlanRun(true, checks, new Dictionary<string, string>()));
        }
        if (dryRun)
        {
            return new PlanRun(true, checks, new Dictionary<string, string>()) { Projects = projects.Report() };
        }

        // Drift is confirmed once for the whole plan (apply --accept-drift), before its first step saves anything.
        await executor.RequireDriftAcceptedAsync(cancellationToken);
        if (BeforeSaving is { } beforeSaving)
        {
            await beforeSaving(cancellationToken);
        }
        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        // Content that isn't the CMS's (Commerce's catalog content), whose $id binds to its ref: id__CatalogContent.
        var createdRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var results = new List<PlanStepResult>();
        try
        {
            // Before the first step saves anything: a project the site won't make stops the plan with nothing saved.
            await projects.CreateAsync(new ProjectWrites(executor), cancellationToken);
        }
        catch (Exception caught)
        {
            var ex = Stopped(caught, cancellationToken);
            throw OptiCliException.Create(ex.Code, $"The plan's project couldn't be made: {ex.Message.TrimEnd('.')}. Nothing was saved.", ex.Hint,
                new PlanRun(false, steps.Select(s => new PlanStepResult(s.Index, s.Operation.Kind, s.Operation.Id, PlanStepStatus.NotRun, Guid: s.Operation.ContentGuid)).ToList(), Refs(created, createdRefs))
                {
                    Projects = projects.Report(),
                });
        }
        // Each step in its project by id; a step marked ready whose content a later step changes again is marked ready
        // once the plan ran (PlanProjects.ReadyAsync), so the later change doesn't leave a second version behind.
        steps = PlanProjects.DeferReady(planned.Select(s => s with { Operation = projects.ForRun(s.Operation) }).ToList());
        var drafts = VersionPerStep ? null : new PlanDrafts();
        foreach (var step in steps)
        {
            var operation = WritePlan.Resolve(step, created, guids, createdRefs);
            try
            {
                // A block an earlier area add was to make shared, where it made none (an inline one).
                if (PlanBlocks.Unresolved(operation, steps, step.Index) is { } unresolved)
                {
                    throw unresolved;
                }
                // A change to a branch this run already saved a draft of goes into that draft.
                var draft = drafts is null ? null : await drafts.ForAsync(operation, session, cancellationToken);
                if (draft is not null)
                {
                    operation = PlanDrafts.Into(operation, draft.Version);
                }
                if (operation is PublishOperation publish)
                {
                    // The blocks earlier steps saved go along with the publish, as with one write that publishes them.
                    operation = publish with { WrittenBlocks = WrittenBlocks(results) };
                }
                var outcome = await executor.RunAsync(operation, dryRun: false, cancellationToken);
                var (into, note) = drafts?.After(operation, step.Index, outcome.Output, draft) ?? default;
                IReadOnlyList<string> warnings = note is null ? outcome.Warnings : [.. outcome.Warnings, note];
                if (step.Operation.Id is { } planId && outcome.CreatedRef is { } createdRef)
                {
                    createdRefs[planId] = createdRef;
                    if (outcome.Output is WriteOutput { Guid: { } catalogGuid })
                    {
                        guids[planId] = catalogGuid;
                    }
                }
                if (step.Operation.Id is { } id && outcome.CreatedId is { } createdId)
                {
                    created[id] = createdId;
                    // An area add's output is its content's, not the block's it made: that one's GUID is read back.
                    if ((PlanBlocks.CreatedGuid(step.Operation, outcome.Output) ?? (PlanBlocks.Names(step.Operation)
                        ? (await session.HeaderAsync(createdId, cancellationToken)).Guid
                        : null)) is { } guid)
                    {
                        guids[id] = guid;
                    }
                }
                results.Add(new PlanStepResult(step.Index, operation.Kind, operation.Id, Changed(outcome.Output) ? PlanStepStatus.Saved : PlanStepStatus.Unchanged,
                    outcome.Output, Undo: UndoHints.For(operation, outcome.Output, into), Warnings: warnings.Count > 0 ? warnings : null,
                    Guid: operation.ContentGuid) { SavedInto = into });
                Progress?.Invoke("applying", results.Count, steps.Count);
                if (drafts?.Ended is { } ended)
                {
                    PlanDrafts.Revise(results, ended.Draft, step.Index, ended.How);
                }
            }
            catch (Exception caught)
            {
                // Whatever stopped the plan (Ctrl+C, a file that went away, a bug), the report of what was saved must survive.
                var ex = Stopped(caught, cancellationToken);
                results.Add(new PlanStepResult(step.Index, operation.Kind, operation.Id, PlanStepStatus.Failed, ex.Details, Error(ex), Guid: operation.ContentGuid));
                results.AddRange(steps.Where(s => s.Index > step.Index).Select(s => new PlanStepResult(s.Index, s.Operation.Kind, s.Operation.Id, PlanStepStatus.NotRun, Guid: s.Operation.ContentGuid)));
                var saved = results.Count(r => r.Status == PlanStepStatus.Saved);
                var notMarked = PlanProjects.NotMarked(steps, planned, step.Index) is { } deferred ? $" {deferred}" : "";
                throw OptiCliException.Create(
                    ex.Code,
                    $"Operation {step.Index} ({operation.Kind}) failed: {ex.Message.TrimEnd('.')}. {saved} earlier operation(s) were saved; details lists them with undo hints.{notMarked}",
                    ex.Hint,
                    new PlanRun(false, results, Refs(created, createdRefs)) { Partial = true, Projects = projects.Report() });
            }
        }
        // A site the plan made (or changed) moves the content below its start page to its hosts: the URLs taken when that
        // content was saved, before the site existed, are wrong now.
        if (results.Any(r => r.Op == "site" && r.Status == PlanStepStatus.Saved))
        {
            results = await WithSiteUrlsAsync(results, cancellationToken);
        }
        try
        {
            await projects.ReadyAsync(new ProjectWrites(executor), planned, results, cancellationToken);
        }
        catch (Exception caught)
        {
            var ex = Stopped(caught, cancellationToken);
            throw OptiCliException.Create(ex.Code,
                $"Every operation was saved, but marking the drafts ready in their project failed: {ex.Message.TrimEnd('.')}.",
                $"Mark them with `opticli projects ready <project> <ref>...` once the problem is fixed ({ex.Hint ?? "see the message"}). details lists what was saved.",
                new PlanRun(false, results, Refs(created, createdRefs)) { Projects = projects.Report() });
        }
        return new PlanRun(false, results, Refs(created, createdRefs)) { Projects = projects.Report() };
    }

    /// <summary>
    /// The steps' results with each content's URL as the sites have it now: <see cref="WriteOutput.Url"/> host-relative on
    /// its site's host and <see cref="WriteOutput.CanonicalUrl"/> with that host, read from the database as <c>opticli
    /// url</c> does. A result whose content has no URL there keeps the one it had, and so does a page whose shortcut is an
    /// external link (<see cref="ExternalLink"/>): the database's rows don't say that, and the link is its URL whatever the sites are.
    /// </summary>
    private async Task<List<PlanStepResult>> WithSiteUrlsAsync(List<PlanStepResult> results, CancellationToken cancellationToken)
    {
        // Fresh caches: the content steps after the site step saved isn't in the ones read then.
        await session.ReloadModelAsync(cancellationToken);
        var ids = results.Select(r => r.Result is WriteOutput { Url: { } link } output && !ExternalLink(link) ? ContentId(output.Ref) : null).OfType<int>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return results;
        }
        await session.Identities.LoadAsync(ids, [], cancellationToken);
        return results.Select(r =>
            r.Result is WriteOutput { Url: { } link } output && !ExternalLink(link) && ContentId(output.Ref) is { } id && session.Identities.Header(id) is { } header
            && session.Identities.Urls.UrlOf(header, output.Language is { } code ? session.Model.LanguageByCode(code) : null) is { } url
                ? r with { Result = output with { Url = url.Path, CanonicalUrl = url.Absolute } }
                : r).ToList();
    }

    /// <summary>
    /// A URL with a scheme or host, as the site's agent gives a page whose shortcut is an external link
    /// (<c>https://www.example.com/docs</c>); every other URL it gives is host-relative (<c>/en/about/</c>).
    /// </summary>
    internal static bool ExternalLink(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("//", StringComparison.Ordinal);

    private static int? ContentId(string? reference) =>
        int.TryParse(reference, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>What stopped a running step, as the error the plan reports it with.</summary>
    internal static OptiCliException Stopped(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OptiCliException known => known,
        OperationCanceledException when cancellationToken.IsCancellationRequested =>
            new CancelledException("interrupted (Ctrl+C) before it finished", Serve.AgentClient.MayHaveSavedHint),
        FileNotFoundException or DirectoryNotFoundException => new NotFoundException(ex.Message, "Check the path."),
        IOException or UnauthorizedAccessException => new UsageException(ex.Message, "Check that the file exists and that you can read it."),
        _ => new InternalException($"{ex.GetType().Name}: {ex.Message}", "This is a bug in opticli; please report it with the plan you ran."),
    };

    /// <summary>
    /// Plan id to ref (<c>id__CatalogContent</c>), for create steps of catalog content whose GUID the site's Commerce agent
    /// finds (<c>--update-existing</c>): the CMS database doesn't have them, so <see cref="ExistingAsync"/> can't. Steps that
    /// depend only on such content (and on <c>existing</c>) get a full dry run.
    /// </summary>
    private IReadOnlyDictionary<string, string> _existingCatalog = new Dictionary<string, string>();

    private async Task<Dictionary<string, string>> ExistingCatalogAsync(IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var catalog = steps
            .Where(s => s.Operation is CreateOperation { Id: { } id, ContentGuid: not null } create && !existing.ContainsKey(id) && executor.IsCatalogType(create.Type))
            .ToList();
        if (catalog.Count == 0)
        {
            return [];
        }
        var found = await executor.CatalogRefsAsync(catalog.Select(s => s.Operation.ContentGuid!.Value.ToString("D")), cancellationToken);
        return catalog
            .Where(s => found.ContainsKey(s.Operation.ContentGuid!.Value.ToString("D")))
            .ToDictionary(s => s.Operation.Id!, s => found[s.Operation.ContentGuid!.Value.ToString("D")].Ref!, StringComparer.Ordinal);
    }

    /// <summary>Plan id to content id, for steps whose GUID already exists outside the recycle bin (<c>--update-existing</c>).</summary>
    private async Task<Dictionary<string, int>> ExistingAsync(IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        var withGuid = steps.Where(s => s.Operation is { Id: not null, ContentGuid: not null }).ToList();
        var ids = await ContentHeaderReader.IdsByGuidsAsync(session.Db, withGuid.Select(s => s.Operation.ContentGuid!.Value), cancellationToken);
        var headers = await ContentHeaderReader.ByIdsAsync(session.Db, ids.Values, cancellationToken);
        return withGuid
            .Where(s => ids.TryGetValue(s.Operation.ContentGuid!.Value, out var id) && headers.TryGetValue(id, out var header) && !header.Deleted)
            .ToDictionary(s => s.Operation.Id!, s => ids[s.Operation.ContentGuid!.Value], StringComparer.Ordinal);
    }

    /// <summary>
    /// For plans that publish, request approval, translate, edit a composition, base a change on another version (<c>from</c>) or name a ContentArea item by its position, the existing
    /// content (and branch) each step writes, so that a step can be dry-run with what earlier steps change on the same
    /// content (<see cref="PlanSimulation.OnExisting"/>), and the order of publishes checked
    /// (<see cref="PlanSimulation.MasterFirst"/>). Steps whose ref can't be resolved are left out; their own dry run reports why.
    /// </summary>
    private async Task<Dictionary<int, PlanTarget>> TargetsAsync(IReadOnlyList<PlanStep> resolved, IReadOnlyDictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var targets = new Dictionary<int, PlanTarget>();
        if (!NeedsTargets(resolved))
        {
            return targets;
        }
        foreach (var step in resolved)
        {
            var (reference, lang) = step.Operation switch
            {
                SetOperation set => (set.Ref, set.Lang),
                AreaEdit area => (area.Ref, area.Lang),
                CompositionEdit composition => (composition.Ref, composition.Lang),
                TranslateOperation translate => (translate.Ref, translate.Lang),
                PublishOperation publish => (publish.Ref, publish.Lang),
                // A create step whose GUID exists updates that content (--update-existing).
                CreateOperation { Id: { } id } create when existing.TryGetValue(id, out var found) => (WriteOutput.Id(found), create.Lang),
                BlockCreateOperation { Id: { } id } block when existing.TryGetValue(id, out var found) => (WriteOutput.Id(found), block.Lang),
                // An upload that gives existing media a new file saves a draft of it, which a later publish puts live.
                UploadOperation { Replace: { } replaced } => (replaced, null),
                UploadOperation { Id: { } id } when existing.TryGetValue(id, out var found) => (WriteOutput.Id(found), null),
                _ => ((string?)null, (string?)null),
            };
            if (reference is null || PlanSimulation.PlanId(reference) is not null)
            {
                continue;
            }
            try
            {
                var located = await session.LocateAsync(reference, null, cancellationToken);
                var header = await session.HeaderAsync(located.Id, cancellationToken);
                var master = session.Model.Language(header.MasterLanguageId);
                var language = session.Language(lang) ?? located.Url?.Language ?? master;
                if (lang is null && step.Operation is PublishOperation publishing && (publishing.Version ?? located.VersionId) is { } version
                    && await VersionReader.ByIdAsync(session.Db, session.Model, version, cancellationToken) is { } named && named.ContentId == located.Id)
                {
                    // A named version is published in its own language.
                    language = session.Model.Language(named.LanguageId) ?? language;
                }
                targets[step.Index] = new PlanTarget(located.Id, language?.Code, master?.Code,
                    BranchExists: language is null || header.Languages.ContainsKey(language.Id),
                    Versioned: located.VersionId is not null || step.Operation is PublishOperation { Version: not null },
                    MasterPublished: header.MasterPublished);
            }
            catch (OptiCliException)
            {
                // Not found, or not a ref: the step's own dry run says so.
            }
        }
        return targets;
    }

    /// <summary>
    /// Whether a plan's dry run needs the existing content each step writes (<see cref="TargetsAsync"/>): it publishes,
    /// requests approval, translates, edits a composition or bases a change on another version, or names a ContentArea
    /// item by its position. Then an earlier area step decides which item that is (and where the site makes new area blocks
    /// shared, whether it is one the step can't change that way).
    /// </summary>
    internal static bool NeedsTargets(IReadOnlyList<PlanStep> steps) =>
        steps.Any(s => s.Operation is PublishOperation or TranslateOperation or SetOperation { From: not null } or AreaEdit { From: not null } or CompositionEdit
            || MayPublish(s.Operation) || PlanSimulation.ByPosition(s.Operation));

    private static bool MayPublish(WriteOperation op) => op.Publishes || op.RequestApproval;

    /// <summary>
    /// For plans that publish: the master language planned content gets where its creating step gives no lang, which is
    /// its parent's (a "For this page" block's: its owner's), for <see cref="PlanSimulation.MasterFirst"/>. Content whose
    /// parent can't be found is left out.
    /// </summary>
    private async Task<Dictionary<string, string>> PlannedMastersAsync(IReadOnlyList<PlanStep> resolved, IReadOnlyDictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var masters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!resolved.Any(s => MayPublish(s.Operation)))
        {
            return masters;
        }
        foreach (var step in resolved)
        {
            var (lang, parent) = step.Operation switch
            {
                CreateOperation create => (create.Lang, create.For ?? create.Parent),
                BlockCreateOperation block => (block.Lang, block.For ?? block.Parent),
                UploadOperation upload => (null, upload.For ?? upload.Parent),
                _ => ((string?)null, (string?)null),
            };
            if (step.Operation.Id is not { } id || existing.ContainsKey(id) || parent is null)
            {
                continue;
            }
            if (lang is not null)
            {
                masters[id] = lang;
            }
            else if (PlanSimulation.PlanId(parent) is { } plannedParent)
            {
                if (masters.TryGetValue(plannedParent, out var inherited))
                {
                    masters[id] = inherited;
                }
            }
            else
            {
                try
                {
                    var located = await session.LocateAsync(parent, null, cancellationToken);
                    if (session.Model.Language((await session.HeaderAsync(located.Id, cancellationToken)).MasterLanguageId) is { } master)
                    {
                        masters[id] = master.Code;
                    }
                }
                catch (OptiCliException)
                {
                    // The step's own dry run says what is wrong with its parent.
                }
            }
        }
        return masters;
    }

    /// <summary>
    /// The block versions the steps run so far saved (<c>941_5108</c>): the blocks of a content's "For this page" folder
    /// their writes made or changed (<see cref="WriteOutput.SharedBlocks"/>, <c>created</c> or <c>updated</c>). A
    /// <c>publish</c> step takes them along unnamed (<see cref="Protocol.PublishRequest.WrittenBlocks"/>), where the
    /// content it publishes shows them from its own folder and they are still the blocks' latest versions; the site ignores
    /// the others. A block an add found already there (<c>existing</c>) wasn't changed by the run, so it isn't one.
    /// </summary>
    internal static IReadOnlyList<string>? WrittenBlocks(IEnumerable<PlanStepResult> results) =>
        results.Select(r => r.Result).OfType<WriteOutput>()
            .SelectMany(o => o.SharedBlocks ?? [])
            .Where(b => b.Action is Protocol.SharedBlockActions.Created or Protocol.SharedBlockActions.Updated && b.Ref is { } reference && reference.Contains('_'))
            .Select(b => b.Ref!)
            .Distinct(StringComparer.Ordinal)
            .ToList() is { Count: > 0 } written ? written : null;

    /// <summary>
    /// Whether a step changed anything: saved a version, or (with none of the content) saved or published blocks of its
    /// folder, as a write that changed only such a block, or a publish of a published version that published just its blocks.
    /// </summary>
    internal static bool Changed(object output) => output switch
    {
        WriteOutput write => write.Saved || write.Restored == true || write.Discarded == true
            || write.SharedBlocks?.Any(b => b.Action is Protocol.SharedBlockActions.Created or Protocol.SharedBlockActions.Updated or Protocol.SharedBlockActions.Published) == true,
        MoveOutput move => move.Moved,
        RestoreOutput restore => restore.Restored,
        AccessOutput access => access.Saved,
        Sites.SiteHostsView site => site.Status != Protocol.SiteHostStatus.Unchanged,
        LanguagesOutput languages => languages.Saved,
        RemoveLanguageOutput removed => removed.Removed,
        Protocol.RelationResult relation => Protocol.CommerceWriteStatus.Changed(relation.Status),
        Protocol.PriceResult price => Protocol.CommerceWriteStatus.Changed(price.Status),
        Protocol.WarehouseResult warehouse => Protocol.CommerceWriteStatus.Changed(warehouse.Status),
        Protocol.InventoryResult inventory => Protocol.CommerceWriteStatus.Changed(inventory.Status),
        Protocol.MarketResult market => Protocol.CommerceWriteStatus.Changed(market.Status),
        _ => true,
    };

    /// <summary>
    /// The step's place in the order of publishes first (<see cref="PlanSimulation.MasterFirst"/>): a branch published
    /// before its master is invalid, and one whose master an earlier step publishes is dry-run as passing that rule.
    /// </summary>
    /// <param name="masters">Planned content's master language (<see cref="PlannedMastersAsync"/>).</param>
    /// <param name="checks">The earlier steps' checks, for <see cref="OnPlannedSite"/>.</param>
    private async Task<(PlanStepResult Result, OptiCliException? Failure)> ValidateAsync(PlanStep step, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing,
        IReadOnlyList<PlanStep> resolved, IReadOnlyDictionary<int, PlanTarget> targets, IReadOnlyDictionary<string, string> masters, IReadOnlyList<PlanStepResult> checks,
        CancellationToken cancellationToken)
    {
        if (OnPlannedSite(step.Operation, steps.Where(s => s.Index < step.Index), checks) is { } planned)
        {
            if (step.Operation is UploadOperation upload)
            {
                MediaFiles.Check(upload.File);
            }
            return (new PlanStepResult(step.Index, step.Operation.Kind, step.Operation.Id, PlanStepStatus.Deferred,
                Warnings: [$"{planned.Url} is on the host {planned.Host}, which operation {planned.Index} gives the site '{planned.Site}': the database doesn't have it there yet, so the site checks this one when the plan runs."],
                Guid: step.Operation.ContentGuid), null);
        }
        if (EnabledEarlier(step, steps) is { } language)
        {
            return (new PlanStepResult(step.Index, step.Operation.Kind, step.Operation.Id, PlanStepStatus.Deferred,
                Warnings: [$"An earlier operation enables the language '{language}', which the database doesn't have enabled yet, so the site checks this one when the plan runs."],
                Guid: step.Operation.ContentGuid), null);
        }
        MasterOrder? order;
        try
        {
            order = PlanSimulation.MasterFirst(resolved.First(s => s.Index == step.Index), resolved, targets, masters);
        }
        catch (ContentValidationException ex)
        {
            return (new PlanStepResult(step.Index, step.Operation.Kind, step.Operation.Id, PlanStepStatus.Invalid, ex.Details, Error(ex), Guid: step.Operation.ContentGuid), ex);
        }
        if (order?.PublishedBy is { } publishedBy)
        {
            PlanStep Marked(PlanStep s) => s.Index == step.Index ? s with { Operation = s.Operation with { MasterPublishedBy = publishedBy } } : s;
            step = Marked(step);
            resolved = resolved.Select(Marked).ToList();
        }
        var (result, failure) = await CheckAsync(step, steps, existing, resolved, targets, cancellationToken);
        return order is null || failure is not null
            ? (result, failure)
            : (result with { Warnings = [order.Note, .. result.Warnings ?? []] }, null);
    }

    /// <param name="existing">Content that steps' GUIDs already name; steps that only depend on it get a full dry run.</param>
    /// <param name="resolved">The steps with <paramref name="existing"/> resolved.</param>
    /// <param name="targets">The existing content each step writes (<see cref="TargetsAsync"/>).</param>
    private async Task<(PlanStepResult Result, OptiCliException? Failure)> CheckAsync(PlanStep step, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing,
        IReadOnlyList<PlanStep> resolved, IReadOnlyDictionary<int, PlanTarget> targets, CancellationToken cancellationToken)
    {
        var op = step.Operation;
        try
        {
            if (step.DependsOn.Count == 0 || step.DependsOn.All(id => existing.ContainsKey(id) || _existingCatalog.ContainsKey(id)))
            {
                var resolvedStep = resolved.First(s => s.Index == step.Index);
                op = resolvedStep.Operation;
                // A restore of what an earlier step deletes: the database doesn't have it in the recycle bin yet.
                if (op is RestoreOperation restore && await DeletedEarlierAsync(restore, step.Index, resolved, cancellationToken))
                {
                    return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Deferred,
                        Warnings: [$"An earlier operation deletes {restore.Ref}, so the site checks the restore when the plan runs."]), null);
                }
                // Earlier steps change this content first; today's database doesn't have their changes yet.
                if (PlanSimulation.OnExisting(resolvedStep, resolved, targets, existing, executor.UpdateExisting) is { } onExisting)
                {
                    var simulated = await SimulateAsync(step, onExisting, steps, existing, cancellationToken);
                    if (simulated.Failure is not null || !PlanSimulation.ChangeNothing(onExisting, simulated.Result.Result as WriteOutput))
                    {
                        return simulated;
                    }
                    // The steps before the publish change nothing, so it meets the content as it is today: dry-run on its own,
                    // as the run will publish it (a conflict where nothing is left to publish).
                    var alone = await executor.RunAsync(op, dryRun: true, cancellationToken);
                    if (alone.Output is WriteOutput { PendingDraft: { } pendingAlone } && !op.IncludeDraft)
                    {
                        throw WriteExecutor.UnconfirmedDraft(pendingAlone, op);
                    }
                    return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Valid, alone.Output,
                        Warnings: [$"Operation(s) {string.Join(", ", onExisting.Folded!.Select(s => s.Index))} change nothing of {op.Refs.First()}, so this publish was dry-run on the content as it is.", .. alone.Warnings],
                        Guid: op.ContentGuid), null);
                }
                (op, var moved) = await WhereItIsAsync(op, step.Index, steps, existing, cancellationToken);
                if (moved is not null)
                {
                    var here = await executor.RunAsync(op, dryRun: true, cancellationToken);
                    if (here.Output is WriteOutput { PendingDraft: { } pendingHere } && !op.IncludeDraft)
                    {
                        throw WriteExecutor.UnconfirmedDraft(pendingHere, op);
                    }
                    return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Simulated, here.Output, Warnings: [moved, .. here.Warnings], Guid: op.ContentGuid), null);
                }
            }
            else
            {
                CheckNamesOfPlannedContent(op, steps);
                await CheckPlannedApprovalAsync(step, steps, cancellationToken);
                if (op is UploadOperation upload)
                {
                    MediaFiles.Check(upload.File);
                }
                if (PlanSimulation.For(step, steps, existing, executor.UpdateExisting) is { } simulation)
                {
                    return await SimulateAsync(step, simulation, steps, existing, cancellationToken);
                }
                (op, var movedFirst) = await WhereItIsAsync(op, step.Index, steps, existing, cancellationToken);
                if (PlanSimulation.WithStandInLinks(op, steps, existing) is { } linking)
                {
                    WriteOutcome linked;
                    try
                    {
                        linked = await executor.RunAsync(linking, dryRun: true, cancellationToken);
                    }
                    catch (ContentValidationException ex) when (ex.Details is WriteOutput { Validation: { } issues } invalid
                        && issues.Where(v => v.Severity == "error").All(v => PlanSimulation.OnlyLinksPlannedContent(v, steps, existing)))
                    {
                        // CMS 13 checks that linked content exists; the plan creates it first.
                        linked = new WriteOutcome<WriteOutput>(invalid with { Validation = issues.Where(v => v.Severity != "error").ToList() is { Count: > 0 } rest ? rest : null }, WriteExecutor.AgentSource, null, []);
                    }
                    if (linked.Output is WriteOutput { PendingDraft: { } pending } && !op.IncludeDraft)
                    {
                        throw WriteExecutor.UnconfirmedDraft(pending, op);
                    }
                    return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Simulated, linked.Output,
                        Warnings: ["Its rich text links to content the plan creates; the dry run checked them with that content's GUID (or a stand-in), and the plan links them when it runs.", .. (movedFirst is null ? [] : (string[])[movedFirst]), .. linked.Warnings],
                        Guid: op.ContentGuid), null);
                }
                if (op is AreaEdit { Action: Protocol.AreaOps.Add } add && await AreaPlacementAsync(add, steps, cancellationToken) is { } checkedAdd)
                {
                    return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Simulated, Warnings: [checkedAdd]), null);
                }
                return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Deferred, Guid: op.ContentGuid), null);
            }
            var outcome = await executor.RunAsync(op, dryRun: true, cancellationToken);
            if (outcome.Output is WriteOutput { PendingDraft: { } draft } && !op.IncludeDraft)
            {
                throw WriteExecutor.UnconfirmedDraft(draft, op);
            }
            var warnings = outcome.Warnings.ToList();
            if (op is DeleteOperation { IgnoreReferences: false } && outcome.Output is MoveOutput { ReferenceCount: > 0 } deleted)
            {
                await CheckReferencesAsync(step, steps, deleted, warnings, cancellationToken);
            }
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Valid, outcome.Output,
                Warnings: warnings.Count > 0 ? warnings : null, Guid: op.ContentGuid), null);
        }
        catch (OptiCliException ex) when (ex.Code is not (ErrorCode.Unreachable or ErrorCode.Internal))
        {
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Invalid, ex.Details, Error(ex), Guid: op.ContentGuid), ex);
        }
    }

    /// <summary>
    /// A URL this step refers to (<c>http://alloy.localhost:5000/siteassets/</c>) on a host that an earlier <c>site</c> step
    /// gives its site, where that step's check didn't find the site as it wants it: the database's sites answer that URL
    /// differently (or not at all) until the step ran, so this one can only be checked then.
    /// </summary>
    /// <param name="earlier">The steps before this one.</param>
    /// <param name="checks">Their checks: a site step found unchanged leaves the database's sites as they are.</param>
    internal static (string Url, string Host, int Index, string Site)? OnPlannedSite(WriteOperation op, IEnumerable<PlanStep> earlier, IReadOnlyList<PlanStepResult> checks)
    {
        var sites = earlier
            .Where(s => s.Operation is SiteOperation
                && checks.FirstOrDefault(c => c.Index == s.Index) is not { Status: PlanStepStatus.Valid, Result: Sites.SiteHostsView { Status: Protocol.SiteHostStatus.Unchanged } })
            .ToList();
        if (sites.Count == 0)
        {
            return null;
        }
        foreach (var reference in op.Refs.OfType<string>())
        {
            if (!Uri.TryCreate(reference.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }
            var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            foreach (var step in sites)
            {
                var site = (SiteOperation)step.Operation;
                if (site.Hosts.Select(h => HostName(h.Name)).FirstOrDefault(h =>
                        string.Equals(h, authority, StringComparison.OrdinalIgnoreCase) || string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)) is { } host)
                {
                    return (reference.Trim(), host, step.Index, site.Name.Trim());
                }
            }
        }
        return null;
    }

    /// <summary>A site step's host as the site gets it: without a scheme (<c>https://localhost:5443</c> is <c>localhost:5443</c>) or a trailing slash.</summary>
    private static string HostName(string name)
    {
        var trimmed = name.Trim();
        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        return (scheme >= 0 ? trimmed[(scheme + 3)..] : trimmed).TrimEnd('/');
    }

    /// <summary>
    /// A language this step writes (or a host of a <c>site</c> step is for) that an earlier <c>language</c> step enables and
    /// the database doesn't have enabled yet: the step can only be checked once that one ran.
    /// </summary>
    private string? EnabledEarlier(PlanStep step, IReadOnlyList<PlanStep> steps)
    {
        var languages = LanguagesOf(step.Operation);
        if (languages.Count == 0)
        {
            return null;
        }
        return steps
            .Where(s => s.Index < step.Index && s.Operation is LanguageOperation { Enabled: true })
            .SelectMany(s => ((LanguageOperation)s.Operation).Langs)
            .FirstOrDefault(code => languages.Contains(code.Trim(), StringComparer.OrdinalIgnoreCase) && session.Model.LanguageByCode(code) is not { Enabled: true });
    }

    private static IReadOnlyList<string> LanguagesOf(WriteOperation op)
    {
        IEnumerable<string?> languages = op switch
        {
            SetOperation set => [set.Lang],
            CreateOperation create => [create.Lang],
            AreaEdit area => [area.Lang],
            CompositionEdit composition => [composition.Lang],
            BlockCreateOperation block => [block.Lang],
            TranslateOperation translate => [translate.Lang],
            PublishOperation publish => [publish.Lang],
            UnpublishOperation unpublish => [unpublish.Lang],
            DiscardOperation discard => [discard.Lang],
            SiteOperation site => site.Hosts.Select(h => h.Language),
            _ => [],
        };
        return languages.OfType<string>().Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    /// <summary>The first move step of each content GUID a plan moves by its GUID.</summary>
    private Dictionary<Guid, PlanStep> Moves(IReadOnlyList<PlanStep> steps) =>
        _moves ??= steps.Where(s => s.Operation is MoveOperation move && Guid.TryParse(move.Ref, out _))
            .GroupBy(s => Guid.Parse(((MoveOperation)s.Operation).Ref)).ToDictionary(g => g.Key, g => g.First());

    private Dictionary<Guid, PlanStep>? _moves;

    /// <summary>
    /// <see cref="MovesFirst"/>: a create, block or upload step on existing content that an earlier move step moves (named by
    /// its GUID), as the dry run checks it: where the content is now, since the move hasn't happened yet. Other steps, and
    /// every step without <see cref="MovesFirst"/>, as they are.
    /// </summary>
    /// <returns>The step to dry-run, and a note for its warnings when it was changed.</returns>
    private async Task<(WriteOperation Operation, string? Note)> WhereItIsAsync(WriteOperation op, int index, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing, CancellationToken cancellationToken)
    {
        if (!MovesFirst || op is not (CreateOperation or BlockCreateOperation or UploadOperation) || op.Id is not { } id || op.ContentGuid is not { } guid
            || !existing.TryGetValue(id, out var contentId)
            || Moves(steps).GetValueOrDefault(guid) is not { } moving || moving.Index >= index
            || (await session.HeaderAsync(contentId, cancellationToken)).ParentId is not { } parent)
        {
            return (op, null);
        }
        var here = WriteOutput.Id(parent);
        WriteOperation atParent = op switch
        {
            CreateOperation create => create with { Parent = here, For = null },
            BlockCreateOperation block => block with { Parent = here, For = null },
            UploadOperation upload => upload with { Parent = here, For = null },
            _ => op,
        };
        return (atParent, $"Dry-run under its parent now, {here}: operation {moving.Index} moves it to this step's place first when the plan runs.");
    }

    /// <summary>
    /// Whether a step before <paramref name="index"/> deletes the content <paramref name="restore"/> restores. Refs are
    /// compared by the content they name (an id, a GUID, a URL or path name the same item), not as text.
    /// </summary>
    private async Task<bool> DeletedEarlierAsync(RestoreOperation restore, int index, IReadOnlyList<PlanStep> resolved, CancellationToken cancellationToken)
    {
        var deletes = resolved.Where(s => s.Index < index).Select(s => s.Operation).OfType<DeleteOperation>().ToList();
        if (deletes.Count == 0)
        {
            return false;
        }
        if (deletes.Any(d => d.Ref == restore.Ref))
        {
            return true;
        }
        var target = await ContentIdAsync(restore.Ref, cancellationToken);
        foreach (var delete in deletes)
        {
            if (target is not null && await ContentIdAsync(delete.Ref, cancellationToken) == target)
            {
                return true;
            }
        }
        return false;
    }

    /// <returns>The content id a ref names; null for planned content (<c>$id</c>) or a ref that doesn't resolve, whose own check says why.</returns>
    private async Task<int?> ContentIdAsync(string reference, CancellationToken cancellationToken)
    {
        if (PlanSimulation.PlanId(reference) is not null)
        {
            return null;
        }
        try
        {
            return (await session.LocateAsync(reference, null, cancellationToken)).Id;
        }
        catch (OptiCliException)
        {
            return null;
        }
    }

    /// <summary>
    /// A delete of referenced content needs <c>"ignoreReferences": true</c>, except for references from content an earlier
    /// step changes, which may remove them: those only warn, and the real run checks again.
    /// </summary>
    /// <exception cref="ConflictException">References that no earlier step can have removed.</exception>
    private async Task CheckReferencesAsync(PlanStep step, IReadOnlyList<PlanStep> steps, MoveOutput deleted, List<string> warnings, CancellationToken cancellationToken)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var earlier in steps.Where(s => s.Index < step.Index))
        {
            var reference = earlier.Operation switch
            {
                SetOperation set => set.Ref,
                AreaEdit area => area.Ref,
                CompositionEdit composition => composition.Ref,
                DeleteOperation delete => delete.Ref,
                MoveOperation move => move.Ref,
                _ => null,
            };
            if (reference is null || PlanSimulation.PlanId(reference) is not null)
            {
                continue;
            }
            try
            {
                changed.Add(WriteOutput.Id((await session.LocateAsync(reference, null, cancellationToken)).Id));
            }
            catch (OptiCliException)
            {
                // That step's own dry run says what is wrong with it.
            }
        }
        var references = deleted.References ?? [];
        var remaining = references.Where(r => !changed.Contains(r.From)).ToList();
        // Past the first Queries.IncomingReferences.Max, it can't be told which ones earlier steps change.
        var unseen = deleted.ReferenceCount!.Value - references.Count;
        if (remaining.Count + unseen > 0)
        {
            throw WriteExecutor.Referenced(
                $"Content {deleted.Ref} is referenced by content the plan doesn't change first ({string.Join(", ", remaining.Take(5).Select(r => $"{r.From} {r.Property}"))}{(unseen > 0 ? $", and {unseen} more" : "")}).",
                new Queries.IncomingReferences(remaining, remaining.Count + unseen));
        }
        warnings.Add($"The references to {deleted.Ref} are all from content that earlier operations change ({string.Join(", ", changed.Intersect(references.Select(r => r.From)))}); the delete checks again when it runs, and stops if they are still there.");
    }

    /// <summary>The step as a dry run against stand-ins; validation errors on the properties it had to leave out don't count.</summary>
    private async Task<(PlanStepResult Result, OptiCliException? Failure)> SimulateAsync(PlanStep step, Simulation simulation, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var op = step.Operation;
        var notes = new List<string>(simulation.Notes ?? []);
        if (simulation.StandIns.Count > 0)
        {
            notes.Add($"Dry-run under stand-in parents, since the plan creates the real ones: {string.Join(", ", simulation.StandIns.Select(s => $"{s.Value} for {s.Key}"))}.");
        }
        if (simulation.Unchecked.Count > 0)
        {
            notes.Add($"Not checked until the plan runs: {string.Join(", ", simulation.Unchecked)} (they refer to content the plan creates, or are area edits the dry run can't apply).");
        }
        var owner = simulation.Target is { } target ? PlanSimulation.TypeOf(target, steps) : null;
        owner ??= (simulation.Operation as CreateOperation)?.Type ?? (simulation.Operation as BlockCreateOperation)?.Type;
        var placements = new List<string>();
        foreach (var (property, planId) in simulation.References)
        {
            if (owner is not null && PlanSimulation.TypeOf(planId, steps) is { } item && Placement(owner, property, item) is { } problem)
            {
                placements.Add(problem);
            }
        }

        WriteOutcome? outcome = null;
        IReadOnlyList<Protocol.ValidationIssue> errors = [];
        object? details = null;
        try
        {
            // What the plan knows about the step's master branch holds for what it is dry-run as.
            // The step's project goes with what it is dry-run as, where that saves a version without publishing it.
            var simulated = simulation.Operation with { MasterPublishedBy = op.MasterPublishedBy };
            var inProject = simulated with { Project = op.Project, Ready = op.Ready, ProjectFromPlan = op.ProjectFromPlan };
            outcome = await executor.RunAsync(op.Project is not null && inProject.ProjectProblem() is null ? inProject : simulated, dryRun: true, cancellationToken);
        }
        catch (ContentValidationException ex) when (ex.Details is WriteOutput invalid)
        {
            // Errors on properties the simulation left out (a required ContentArea, say) say nothing about the plan.
            // So are URL segment clashes under a stand-in parent: the segment only has to be unique among the real siblings.
            errors = (invalid.Validation ?? [])
                .Where(v => v.Severity == "error" && !Left(v.Property, simulation.Unchecked))
                .Where(v => simulation.StandIns.Count == 0 || !string.Equals(v.Property, "PageURLSegment", StringComparison.OrdinalIgnoreCase))
                .Where(v => !PlanSimulation.OnlyLinksPlannedContent(v, steps, existing))
                .ToList();
            details = invalid with { Validation = errors };
            if (errors.Count < (invalid.Validation ?? []).Count(v => v.Severity == "error"))
            {
                notes.Add("Validation errors that only the stand-in causes were ignored (on properties not checked yet, a URL segment clash with the stand-in's children, or a link to content the plan creates first).");
            }
        }

        if (outcome?.Output is WriteOutput { PendingDraft: { } draft } && !op.IncludeDraft)
        {
            // As for a step that is dry-run as is: the publish would put someone else's changes live unconfirmed.
            var unconfirmed = WriteExecutor.UnconfirmedDraft(draft, op);
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Invalid, unconfirmed.Details, Error(unconfirmed), Warnings: notes, Guid: op.ContentGuid), unconfirmed);
        }
        if (errors.Count == 0 && PlanBlocks.OnPlannedBlock(op, outcome?.Output as WriteOutput) is { } onBlock)
        {
            // Dry-run in one write with the steps before it, where a new block's values can still be changed by position.
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Invalid, outcome?.Output, Error(onBlock), Warnings: notes, Guid: op.ContentGuid), onBlock);
        }
        var messages = errors.Select(v => v.Property is null ? v.Message : $"{v.Property}: {v.Message}").Concat(placements).ToList();
        if (messages.Count > 0)
        {
            var failure = new ContentValidationException(
                $"Dry run (as it will be after operation {step.Index}): the change would fail validation ({string.Join("; ", messages)}). Nothing was saved.",
                "details has the dry-run result; fix the listed properties and retry. Content the plan creates is checked under an existing stand-in parent.")
            { Details = details ?? outcome?.Output };
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Invalid, failure.Details, Error(failure), Warnings: notes, Guid: op.ContentGuid), failure);
        }
        var warnings = notes.Concat(outcome?.Warnings ?? []).ToList();
        return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Simulated, outcome?.Output ?? details,
            Warnings: warnings.Count > 0 ? warnings : null, Guid: op.ContentGuid), null);
    }

    private static bool Left(string? property, IReadOnlyList<string> skipped) =>
        property is not null && skipped.Any(u => property.Equals(u, StringComparison.OrdinalIgnoreCase) || property.StartsWith(u + ".", StringComparison.OrdinalIgnoreCase) || property.StartsWith(u + "[", StringComparison.OrdinalIgnoreCase));

    /// <summary>An <c>area add</c> on planned content, or of a planned item: checked against <c>[AllowedTypes]</c> in the code.</summary>
    /// <returns>A note when it could be checked and passed; null when it couldn't be checked.</returns>
    /// <exception cref="ContentValidationException">The code doesn't allow the item there.</exception>
    private async Task<string?> AreaPlacementAsync(AreaEdit add, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        if (allowedTypes is null || (add.Item is null && add.Type is null))
        {
            return null;
        }
        var owner = await TypeOfAsync(add.Ref, steps, cancellationToken);
        // A new inline block's type is the step's own, by its name as the site has it.
        var item = add.Type is { } type
            ? InlineTypeName(type)
            : await TypeOfAsync(add.Item!, steps, cancellationToken);
        if (owner is null || item is null)
        {
            return null;
        }
        if (Placement(owner, add.Property, item) is { } problem)
        {
            throw new ContentValidationException($"Dry run: {problem}", "Pick a ContentArea that allows the type (`opticli allowed-in <type>`), or another block type.");
        }
        return $"{item} is allowed in {owner}.{add.Property} by [AllowedTypes] in the code; the CMS validates the placement when the plan runs.";
    }

    /// <summary>The name of the block type an inline add names (by name, GUID or id); null for none (the step reports it).</summary>
    private string? InlineTypeName(string type)
    {
        try
        {
            return PropertyNameCheck.BlockType(session.Model, type, "type").Name;
        }
        catch (UsageException)
        {
            return null;
        }
    }

    /// <returns>Why the code doesn't allow <paramref name="item"/> in the property; null when it does or can't tell.</returns>
    private string? Placement(string owner, string property, string item)
    {
        var ownerType = session.Model.Types.FirstOrDefault(t => t.Name.Equals(owner, StringComparison.OrdinalIgnoreCase));
        var definition = ownerType is null ? null : session.Model.PropertiesOf(ownerType.Id).FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !Queries.AllowedInQuery.IsReference(definition))
        {
            return null;
        }
        return allowedTypes?.Allows(owner, definition.Name, item) == false
            ? $"{owner}.{definition.Name} doesn't allow {item}, by [AllowedTypes] in the code (see `opticli allowed-in {item}`)."
            : null;
    }

    /// <summary>The content type of a plan id (as planned) or of existing content; null when unknown.</summary>
    private async Task<string?> TypeOfAsync(string reference, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        if (PlanSimulation.PlanId(reference) is { } id)
        {
            return PlanSimulation.TypeOf(id, steps);
        }
        try
        {
            var located = await session.LocateAsync(reference, null, cancellationToken);
            return session.Model.TypeName((await session.HeaderAsync(located.Id, cancellationToken)).TypeId);
        }
        catch (OptiCliException)
        {
            return null;
        }
    }

    /// <summary>
    /// A step that publishes content the plan creates: the approval sequence that will apply is the nearest existing
    /// ancestor's, since a plan can't define one. The agent can't check it before the content exists.
    /// </summary>
    /// <exception cref="OptiCliException">The rules of <see cref="ApprovalRules.Check"/>.</exception>
    private async Task CheckPlannedApprovalAsync(PlanStep step, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        var op = step.Operation;
        if (!op.Publishes && !op.RequestApproval && !op.Ready)
        {
            return;
        }
        var reference = op switch
        {
            SetOperation set => set.Ref,
            AreaEdit area => area.Ref,
            CompositionEdit composition => composition.Ref,
            TranslateOperation translate => translate.Ref,
            PublishOperation publish => publish.Ref,
            _ => ParentOf(op),
        };
        // Up through the planned content to the first item that exists.
        for (var hops = 0; reference is not null && PlanSimulation.PlanId(reference) is { } id && hops <= steps.Count; hops++)
        {
            reference = PlanSimulation.Index(steps).Operation(id) is { } creating ? ParentOf(creating) : null;
        }
        // Catalog content (Commerce) has no approval sequences, and isn't in the CMS database.
        if (reference is null || PlanSimulation.PlanId(reference) is not null || Protocol.CatalogRefSyntax.IsCommerceOnly(reference) || Protocol.CatalogRefSyntax.ProviderId(reference) is not null)
        {
            return;
        }
        try
        {
            // Many steps of one plan share their nearest existing ancestor (a sync's new pages, say): read once.
            if (!_approvals.TryGetValue(reference, out var sequence))
            {
                var located = await session.LocateAsync(reference, null, cancellationToken);
                var header = await session.HeaderAsync(located.Id, cancellationToken);
                _approvals[reference] = sequence = await Queries.ApprovalReader.ResolveAsync(session.Db, session.Model, header, cancellationToken);
            }
            ApprovalRules.Check(sequence, op, $"The content operations[{step.Index}] {(op.Publishes || !op.Ready ? "publishes" : "marks Ready to publish")}");
        }
        catch (NotFoundException)
        {
            // The step's own checks say so.
        }
    }

    /// <summary>Where a creating step puts its content; null for a "For this page" folder, which only exists once it runs.</summary>
    private static string? ParentOf(WriteOperation op) => op switch
    {
        CreateOperation { Parent: { } parent } => parent,
        BlockCreateOperation { Parent: { } parent } => parent,
        UploadOperation { Parent: { } parent } => parent,
        _ => null,
    };

    /// <summary>For a step on content an earlier step creates, the property names can still be checked against that step's type.</summary>
    private void CheckNamesOfPlannedContent(WriteOperation op, IReadOnlyList<PlanStep> steps)
    {
        (string? reference, System.Text.Json.Nodes.JsonObject? properties, string? area) = op switch
        {
            SetOperation set => (set.Ref, set.Properties, null),
            TranslateOperation translate => (translate.Ref, translate.Properties, null),
            AreaEdit edit => (edit.Ref, null, edit.Property),
            _ => ((string?)null, (System.Text.Json.Nodes.JsonObject?)null, (string?)null),
        };
        if (reference is null || !reference.StartsWith('$'))
        {
            return;
        }
        var typeName = PlanSimulation.Index(steps).Operation(reference[1..]) switch
        {
            CreateOperation create => create.Type,
            BlockCreateOperation block => block.Type,
            UploadOperation upload => upload.Type,
            // The block an area add makes, by its type as the site has it (a name, id or GUID).
            AreaEdit { Type: { } blockType } => InlineTypeName(blockType),
            _ => null,
        };
        // A create from a blueprint (CMS 13) has the blueprint's type, which the site checks.
        if (string.IsNullOrEmpty(typeName))
        {
            return;
        }
        var type = session.Model.RequireType(typeName);
        PropertyNameCheck.Check(session.Model, type.Id, CompositionInput.Split(session.Model, type.Id, properties).Properties);
        if (area is not null)
        {
            PropertyNameCheck.RequireContentArea(session.Model, type.Id, area);
        }
    }

    /// <summary>
    /// An area add with an <c>id</c> on a site that makes new area blocks inline: the block has no ref for later steps to
    /// reach as <c>$id</c>. Invalid when one does; else the id is only noted.
    /// </summary>
    internal static (PlanStepResult Result, OptiCliException? Failure) NamedInline(PlanStep step, IReadOnlyList<PlanStep> steps, PlanStepResult result)
    {
        var id = step.Operation.Id!;
        var users = steps.Where(s => s.Index > step.Index && s.DependsOn.Contains(id)).Select(s => s.Index).ToList();
        var area = ((AreaEdit)step.Operation).Property;
        if (users.Count == 0)
        {
            return (result with { Warnings = [.. result.Warnings ?? [], $"\"id\" '{id}' names nothing here: the site makes new blocks in {area} inline (its edit UI does), and an inline block has no ref."] }, null);
        }
        var failure = new UsageException(
            $"Operation(s) {string.Join(", ", users)} reach the block this add makes as '${id}', but the site makes new blocks in a ContentArea inline (its edit UI does: UIOptions.InlineBlocksInContentAreaEnabled is on; `opticli serve --status` shows inlineBlocks), and an inline block has no ref.",
            "Change the block by its position instead (\"MainArea[2]\": {...} in a set, or an area set), or give its values in the add, and leave out the add's \"id\".");
        return (result with { Status = PlanStepStatus.Invalid, Error = Error(failure) }, failure);
    }

    /// <summary>
    /// An area add with an <c>id</c> whose dry run found an inline block like it in the area already (a plan run again with
    /// <c>--update-existing</c>, on a site that makes shared blocks but kept an inline block an earlier run or another tool
    /// made): the add makes no block, so there is nothing for <c>$id</c> to reach. Invalid when a later step uses it, rather
    /// than failing halfway through the run; else the id is only noted.
    /// </summary>
    internal static (PlanStepResult Result, OptiCliException? Failure) FoundInline(PlanStep step, IReadOnlyList<PlanStep> steps, PlanStepResult result)
    {
        var id = step.Operation.Id!;
        var users = steps.Where(s => s.Index > step.Index && s.DependsOn.Contains(id)).Select(s => s.Index).ToList();
        var area = ((AreaEdit)step.Operation).Property;
        if (users.Count == 0)
        {
            return (result with { Warnings = [.. result.Warnings ?? [], $"\"id\" '{id}' names nothing here: {area} already has an inline block like the one this add makes, so the add leaves it as it is, and an inline block has no ref."] }, null);
        }
        var failure = new UsageException(
            $"Operation(s) {string.Join(", ", users)} reach the block this add makes as '${id}', but {area} already has an inline block like it (the add only adds what is missing, --update-existing), so the add makes no shared block and '${id}' names nothing. An inline block has no ref.",
            $"Change that inline block by its position instead (\"{area}[n]\": {{...}} in a set, or an area set), and leave out the add's \"id\"; or remove the inline block from the area first, so the add makes a shared block. Nothing was changed.");
        return (result with { Status = PlanStepStatus.Invalid, Error = Error(failure) }, failure);
    }

    private static ErrorBody Error(OptiCliException ex) => new(ExitCodes.Name(ex.Code), ex.Message, ex.Hint);

    private static Dictionary<string, string> Refs(Dictionary<string, int> created, Dictionary<string, string> createdRefs) =>
        created.Select(c => KeyValuePair.Create(c.Key, WriteOutput.Id(c.Value))).Concat(createdRefs).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
}
