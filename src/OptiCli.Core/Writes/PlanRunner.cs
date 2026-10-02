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
    string Status,
    object? Result = null,
    ErrorBody? Error = null,
    string? Undo = null,
    IReadOnlyList<string>? Warnings = null,
    Guid? Guid = null);

/// <param name="Created">Plan id to content ref, for every created item.</param>
public sealed record PlanRun(bool DryRun, IReadOnlyList<PlanStepResult> Operations, IReadOnlyDictionary<string, string> Created)
{
    /// <summary>True when the plan stopped before its last operation; <see cref="Operations"/> says which were saved.</summary>
    public bool? Partial { get; init; }
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
    /// <exception cref="OptiCliException">
    /// Validation failed (nothing written), or a step failed during execution; <c>details</c> is the
    /// <see cref="PlanRun"/> with each step's status, results of the saved steps and undo hints.
    /// </exception>
    public async Task<PlanRun> RunAsync(WritePlan plan, bool dryRun, bool publishAll, CancellationToken cancellationToken)
    {
        // Rich text from @file values only has its $ids once the files are read.
        var steps = WritePlan.LinkText(PlanFiles.Resolve(plan.Steps
            .Select(s => publishAll ? s with { Operation = s.Operation.WithPublish() } : s)
            .Select(s => requestApproval ? s with { Operation = s.Operation.WithRequestApproval() } : s), planDirectory, allowOutside));
        var guids = WritePlan.FixedGuids(steps);

        var existing = executor.UpdateExisting ? await ExistingAsync(steps, cancellationToken) : new Dictionary<string, int>();
        var resolved = steps.Select(s => s with { Operation = WritePlan.Resolve(s, existing, guids) }).ToList();
        var targets = await TargetsAsync(resolved, existing, cancellationToken);
        var masters = await PlannedMastersAsync(resolved, existing, cancellationToken);
        var checks = new List<PlanStepResult>();
        OptiCliException? firstFailure = null;
        foreach (var step in steps)
        {
            var (result, failure) = await ValidateAsync(step, steps, existing, resolved, targets, masters, cancellationToken);
            checks.Add(result);
            firstFailure ??= failure;
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
            return new PlanRun(true, checks, new Dictionary<string, string>());
        }

        // Drift is confirmed once for the whole plan (apply --accept-drift), before its first step saves anything.
        await executor.RequireDriftAcceptedAsync(cancellationToken);
        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<PlanStepResult>();
        foreach (var step in steps)
        {
            var operation = WritePlan.Resolve(step, created, guids);
            try
            {
                var outcome = await executor.RunAsync(operation, dryRun: false, cancellationToken);
                if (step.Operation.Id is { } id && outcome.CreatedId is { } createdId)
                {
                    created[id] = createdId;
                    if (outcome.Output is WriteOutput { Guid: { } guid })
                    {
                        guids[id] = guid;
                    }
                }
                results.Add(new PlanStepResult(step.Index, operation.Kind, operation.Id, Changed(outcome.Output) ? PlanStepStatus.Saved : PlanStepStatus.Unchanged,
                    outcome.Output, Undo: UndoHints.For(operation, outcome.Output), Warnings: outcome.Warnings.Count > 0 ? outcome.Warnings : null,
                    Guid: operation.ContentGuid));
            }
            catch (Exception caught)
            {
                // Whatever stopped the plan (Ctrl+C, a file that went away, a bug), the report of what was saved must survive.
                var ex = Stopped(caught, cancellationToken);
                results.Add(new PlanStepResult(step.Index, operation.Kind, operation.Id, PlanStepStatus.Failed, ex.Details, Error(ex), Guid: operation.ContentGuid));
                results.AddRange(steps.Where(s => s.Index > step.Index).Select(s => new PlanStepResult(s.Index, s.Operation.Kind, s.Operation.Id, PlanStepStatus.NotRun, Guid: s.Operation.ContentGuid)));
                var saved = results.Count(r => r.Status == PlanStepStatus.Saved);
                throw OptiCliException.Create(
                    ex.Code,
                    $"Operation {step.Index} ({operation.Kind}) failed: {ex.Message.TrimEnd('.')}. {saved} earlier operation(s) were saved; details lists them with undo hints.",
                    ex.Hint,
                    new PlanRun(false, results, Refs(created)) { Partial = true });
            }
        }
        return new PlanRun(false, results, Refs(created));
    }

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
    /// For plans that publish, request approval, translate or base a change on another version (<c>from</c>), the existing
    /// content (and branch) each step writes, so that a step can be dry-run with what earlier steps change on the same
    /// content (<see cref="PlanSimulation.OnExisting"/>), and the order of publishes checked
    /// (<see cref="PlanSimulation.MasterFirst"/>). Steps whose ref can't be resolved are left out; their own dry run reports why.
    /// </summary>
    private async Task<Dictionary<int, PlanTarget>> TargetsAsync(IReadOnlyList<PlanStep> resolved, IReadOnlyDictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var targets = new Dictionary<int, PlanTarget>();
        if (!resolved.Any(s => s.Operation is PublishOperation or TranslateOperation or SetOperation { From: not null } or AreaEdit { From: not null } || MayPublish(s.Operation)))
        {
            return targets;
        }
        foreach (var step in resolved)
        {
            var (reference, lang) = step.Operation switch
            {
                SetOperation set => (set.Ref, set.Lang),
                AreaEdit area => (area.Ref, area.Lang),
                TranslateOperation translate => (translate.Ref, translate.Lang),
                PublishOperation publish => (publish.Ref, publish.Lang),
                // A create step whose GUID exists updates that content (--update-existing).
                CreateOperation { Id: { } id } create when existing.TryGetValue(id, out var found) => (WriteOutput.Id(found), create.Lang),
                BlockCreateOperation { Id: { } id } block when existing.TryGetValue(id, out var found) => (WriteOutput.Id(found), block.Lang),
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
                CreateOperation create => (create.Lang, create.Parent),
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

    private static bool Changed(object output) => output switch
    {
        WriteOutput write => write.Saved || write.Restored == true || write.Discarded == true,
        MoveOutput move => move.Moved,
        AccessOutput access => access.Saved,
        RemoveLanguageOutput removed => removed.Removed,
        _ => true,
    };

    /// <summary>
    /// The step's place in the order of publishes first (<see cref="PlanSimulation.MasterFirst"/>): a branch published
    /// before its master is invalid, and one whose master an earlier step publishes is dry-run as passing that rule.
    /// </summary>
    /// <param name="masters">Planned content's master language (<see cref="PlannedMastersAsync"/>).</param>
    private async Task<(PlanStepResult Result, OptiCliException? Failure)> ValidateAsync(PlanStep step, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing,
        IReadOnlyList<PlanStep> resolved, IReadOnlyDictionary<int, PlanTarget> targets, IReadOnlyDictionary<string, string> masters, CancellationToken cancellationToken)
    {
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
            if (step.DependsOn.Count == 0 || step.DependsOn.All(existing.ContainsKey))
            {
                var resolvedStep = resolved.First(s => s.Index == step.Index);
                op = resolvedStep.Operation;
                // Earlier steps change this content first; today's database doesn't have their changes yet.
                if (PlanSimulation.OnExisting(resolvedStep, resolved, targets, existing, executor.UpdateExisting) is { } onExisting)
                {
                    return await SimulateAsync(step, onExisting, steps, cancellationToken);
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
                    return await SimulateAsync(step, simulation, steps, cancellationToken);
                }
                if (PlanSimulation.WithStandInLinks(op, steps, existing) is { } linking)
                {
                    var linked = await executor.RunAsync(linking, dryRun: true, cancellationToken);
                    if (linked.Output is WriteOutput { PendingDraft: { } pending } && !op.IncludeDraft)
                    {
                        throw WriteExecutor.UnconfirmedDraft(pending, op);
                    }
                    return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Simulated, linked.Output,
                        Warnings: ["Its rich text links to content the plan creates; the dry run checked them with that content's GUID (or a stand-in), and the plan links them when it runs.", .. linked.Warnings],
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
    private async Task<(PlanStepResult Result, OptiCliException? Failure)> SimulateAsync(PlanStep step, Simulation simulation, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
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
            outcome = await executor.RunAsync(simulation.Operation with { MasterPublishedBy = op.MasterPublishedBy }, dryRun: true, cancellationToken);
        }
        catch (ContentValidationException ex) when (ex.Details is WriteOutput invalid)
        {
            // Errors on properties the simulation left out (a required ContentArea, say) say nothing about the plan.
            // So are URL segment clashes under a stand-in parent: the segment only has to be unique among the real siblings.
            errors = (invalid.Validation ?? [])
                .Where(v => v.Severity == "error" && !Left(v.Property, simulation.Unchecked))
                .Where(v => simulation.StandIns.Count == 0 || !string.Equals(v.Property, "PageURLSegment", StringComparison.OrdinalIgnoreCase))
                .ToList();
            details = invalid with { Validation = errors };
            if (errors.Count < (invalid.Validation ?? []).Count(v => v.Severity == "error"))
            {
                notes.Add("Validation errors that only the stand-in causes were ignored (on properties not checked yet, or a URL segment clash with the stand-in's children).");
            }
        }

        if (outcome?.Output is WriteOutput { PendingDraft: { } draft } && !op.IncludeDraft)
        {
            // As for a step that is dry-run as is: the publish would put someone else's changes live unconfirmed.
            var unconfirmed = WriteExecutor.UnconfirmedDraft(draft, op);
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Invalid, unconfirmed.Details, Error(unconfirmed), Warnings: notes, Guid: op.ContentGuid), unconfirmed);
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
        if (allowedTypes is null || add.Item is null)
        {
            return null;
        }
        var owner = await TypeOfAsync(add.Ref, steps, cancellationToken);
        var item = await TypeOfAsync(add.Item, steps, cancellationToken);
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
        if (!op.Publishes && !op.RequestApproval)
        {
            return;
        }
        var reference = op switch
        {
            SetOperation set => set.Ref,
            AreaEdit area => area.Ref,
            TranslateOperation translate => translate.Ref,
            PublishOperation publish => publish.Ref,
            _ => ParentOf(op),
        };
        // Up through the planned content to the first item that exists.
        for (var hops = 0; reference is not null && PlanSimulation.PlanId(reference) is { } id && hops <= steps.Count; hops++)
        {
            reference = steps.Select(s => s.Operation).FirstOrDefault(o => o.Id == id) is { } creating ? ParentOf(creating) : null;
        }
        if (reference is null || PlanSimulation.PlanId(reference) is not null)
        {
            return;
        }
        try
        {
            var located = await session.LocateAsync(reference, null, cancellationToken);
            var header = await session.HeaderAsync(located.Id, cancellationToken);
            var sequence = await Queries.ApprovalReader.ResolveAsync(session.Db, session.Model, header, cancellationToken);
            ApprovalRules.Check(sequence, op, $"The content operations[{step.Index}] publishes");
        }
        catch (NotFoundException)
        {
            // The step's own checks say so.
        }
    }

    /// <summary>Where a creating step puts its content; null for a "For this page" folder, which only exists once it runs.</summary>
    private static string? ParentOf(WriteOperation op) => op switch
    {
        CreateOperation create => create.Parent,
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
        var typeName = steps.Select(s => s.Operation).FirstOrDefault(o => o.Id == reference[1..]) switch
        {
            CreateOperation create => create.Type,
            BlockCreateOperation block => block.Type,
            UploadOperation upload => upload.Type,
            _ => null,
        };
        if (typeName is null)
        {
            return;
        }
        var type = session.Model.RequireType(typeName);
        PropertyNameCheck.Check(session.Model, type.Id, properties);
        if (area is not null)
        {
            PropertyNameCheck.RequireContentArea(session.Model, type.Id, area);
        }
    }

    private static ErrorBody Error(OptiCliException ex) => new(ExitCodes.Name(ex.Code), ex.Message, ex.Hint);

    private static Dictionary<string, string> Refs(Dictionary<string, int> created) =>
        created.ToDictionary(c => c.Key, c => WriteOutput.Id(c.Value), StringComparer.Ordinal);
}
