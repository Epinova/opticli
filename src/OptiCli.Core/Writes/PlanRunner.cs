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
public sealed record PlanRun(bool DryRun, IReadOnlyList<PlanStepResult> Operations, IReadOnlyDictionary<string, string> Created);

/// <summary>
/// Runs a <see cref="WritePlan"/>: validates every step first (nothing is written if any fails), then
/// executes them in order, stopping at the first failure with what was saved and how to undo it.
/// </summary>
/// <param name="planDirectory">The plan file's folder, which files in the plan are relative to (the working directory for stdin).</param>
/// <param name="allowOutside">Allow files outside <paramref name="planDirectory"/> (see <see cref="PlanFiles"/>).</param>
public sealed class PlanRunner(ContentSession session, WriteExecutor executor, string planDirectory, bool allowOutside = false)
{
    /// <exception cref="OptiCliException">
    /// Validation failed (nothing written), or a step failed during execution; <c>details</c> is the
    /// <see cref="PlanRun"/> with each step's status, results of the saved steps and undo hints.
    /// </exception>
    public async Task<PlanRun> RunAsync(WritePlan plan, bool dryRun, bool publishAll, CancellationToken cancellationToken)
    {
        var steps = PlanFiles.Resolve(plan.Steps.Select(s => publishAll ? s with { Operation = s.Operation.WithPublish() } : s), planDirectory, allowOutside);

        var existing = executor.UpdateExisting ? await ExistingAsync(steps, cancellationToken) : new Dictionary<string, int>();
        var checks = new List<PlanStepResult>();
        OptiCliException? firstFailure = null;
        foreach (var step in steps)
        {
            var (result, failure) = await ValidateAsync(step, steps, existing, cancellationToken);
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

        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<PlanStepResult>();
        foreach (var step in steps)
        {
            var operation = WritePlan.Resolve(step, created);
            try
            {
                var outcome = await executor.RunAsync(operation, dryRun: false, cancellationToken);
                if (step.Operation.Id is { } id && outcome.CreatedId is { } createdId)
                {
                    created[id] = createdId;
                }
                results.Add(new PlanStepResult(step.Index, operation.Kind, operation.Id, Changed(outcome.Output) ? PlanStepStatus.Saved : PlanStepStatus.Unchanged,
                    outcome.Output, Undo: UndoHints.For(operation, outcome.Output), Warnings: outcome.Warnings.Count > 0 ? outcome.Warnings : null,
                    Guid: operation.ContentGuid));
            }
            catch (OptiCliException ex)
            {
                results.Add(new PlanStepResult(step.Index, operation.Kind, operation.Id, PlanStepStatus.Failed, ex.Details, Error(ex), Guid: operation.ContentGuid));
                results.AddRange(steps.Where(s => s.Index > step.Index).Select(s => new PlanStepResult(s.Index, s.Operation.Kind, s.Operation.Id, PlanStepStatus.NotRun, Guid: s.Operation.ContentGuid)));
                var saved = results.Count(r => r.Status == PlanStepStatus.Saved);
                throw OptiCliException.Create(
                    ex.Code,
                    $"Operation {step.Index} ({operation.Kind}) failed: {ex.Message.TrimEnd('.')}. {saved} earlier operation(s) were saved; details lists them with undo hints.",
                    ex.Hint,
                    new PlanRun(false, results, Refs(created)));
            }
        }
        return new PlanRun(false, results, Refs(created));
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

    private static bool Changed(object output) => output switch
    {
        WriteOutput write => write.Saved || write.Restored == true,
        MoveOutput move => move.Moved,
        AccessOutput access => access.Saved,
        _ => true,
    };

    /// <param name="existing">Content that steps' GUIDs already name; steps that only depend on it get a full dry run.</param>
    private async Task<(PlanStepResult Result, OptiCliException? Failure)> ValidateAsync(PlanStep step, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing, CancellationToken cancellationToken)
    {
        var op = step.Operation;
        try
        {
            if (step.DependsOn.Count > 0 && step.DependsOn.All(existing.ContainsKey))
            {
                op = WritePlan.Resolve(step, existing);
            }
            else if (step.DependsOn.Count > 0)
            {
                CheckNamesOfPlannedContent(op, steps);
                if (op is UploadOperation upload)
                {
                    MediaFiles.Check(upload.File);
                }
                return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Deferred, Guid: op.ContentGuid), null);
            }
            var outcome = await executor.RunAsync(op, dryRun: true, cancellationToken);
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Valid, outcome.Output,
                Warnings: outcome.Warnings.Count > 0 ? outcome.Warnings : null, Guid: op.ContentGuid), null);
        }
        catch (OptiCliException ex) when (ex.Code is not (ErrorCode.Unreachable or ErrorCode.Internal))
        {
            return (new PlanStepResult(step.Index, op.Kind, op.Id, PlanStepStatus.Invalid, ex.Details, Error(ex), Guid: op.ContentGuid), ex);
        }
    }

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
