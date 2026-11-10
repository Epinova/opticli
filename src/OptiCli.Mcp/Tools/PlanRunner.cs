using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EPiServer.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>What checking or running one step gave (<see cref="IContentPlanSteps"/>).</summary>
/// <param name="Report">What the step's result says, in the shape of the built-in tool's result for the same change.</param>
internal sealed record StepOutcome(JsonObject Report)
{
    /// <summary>False for a dry run that found validation errors: the plan can't run.</summary>
    public bool Valid { get; init; } = true;

    /// <summary>What the plan token covers besides the step itself: what the content was when it was checked.</summary>
    public JsonObject? Facts { get; init; }

    /// <summary>The content the step saved or acted on: for a create, the new item, which later steps use.</summary>
    public ContentReference? Content { get; init; }

    /// <summary>For a run: <c>saved</c>, <c>unchanged</c>, <c>moved</c> or <c>trashed</c>.</summary>
    public string? Status { get; init; }
}

/// <summary>Checks and runs the steps of a plan against the CMS, as the editor; a test puts its own in its place.</summary>
internal interface IContentPlanSteps
{
    /// <summary>
    /// The step's dry run: what it would do, and every check that can be made now. The step has no
    /// <see cref="PlannedContent"/> in it (<see cref="PlanRunner"/> took it out); a parent or destination the plan creates
    /// is replaced by the existing content it will be made below, with the planned content's type beside it.
    /// </summary>
    /// <exception cref="Exception">The step would be refused: as the operation refuses it (<see cref="Cms.AgentException"/>, …).</exception>
    StepOutcome Check(PlanStep step);

    /// <summary>The ids of the content <paramref name="link"/> is below, whatever the editor may read (for comparisons only, never shown).</summary>
    IReadOnlySet<int> AncestorIds(ContentReference link);

    /// <summary>Saves the step, its <see cref="PlannedContent"/> replaced by the content the earlier steps made.</summary>
    /// <param name="facts">What <see cref="Check"/> found for it, just before.</param>
    StepOutcome Run(PlanStep step, JsonObject? facts);
}

/// <summary>
/// The dry run and the real run of a write tool's plan: checks every step, gives the plan token, and runs the steps in
/// order, stopping at the first that fails. Knows nothing of the CMS (<see cref="IContentPlanSteps"/> does).
/// </summary>
/// <remarks>
/// <para>
/// A step that uses content an earlier step creates can't be checked whole before that step has run. Its dry run
/// checks the rest: a create or move below planned content is checked below the existing content the planned item will
/// be made below (its access rights are inherited from there), against the planned item's type for "allowed below";
/// property values and ContentArea items that refer to planned content are left out of it and listed under
/// <c>unchecked</c>. The operation checks them when the step runs.
/// </para>
/// <para>
/// The plan token is a hash, protected with the site's Data Protection keys, of the tool, the editor, the arguments as
/// the tool read them, and every step with what its check found (the version an update is based on, a move's current
/// parent, the number of items a trash takes along). The real run plans and checks again, and runs only when that hash is
/// the same. A token works for <see cref="TokenLifetime"/> after its dry run: an older one is a <c>conflict</c> asking for
/// a new dry run.
/// </para>
/// </remarks>
internal sealed class PlanRunner(
    string tool, ContentPlanKinds kinds, int maxSteps, IContentPlanSteps cms, string editor, string client, McpAudit? audit, ILogger logger, IDataProtectionProvider protection,
    TimeProvider time)
{
    /// <summary>The Data Protection purpose of plan tokens.</summary>
    internal const string TokenPurpose = "OptiCli.Mcp.PlanToken";

    /// <summary>How long a dry run's plan token works.</summary>
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    private readonly IDataProtector _tokens = protection.CreateProtector(TokenPurpose, tool);

    private sealed record Checked(PlanStep Step, StepOutcome? Outcome, JsonObject? Unchecked, AgentError? Error)
    {
        public bool Runnable => Error is null && Outcome is { Valid: true };
    }

    /// <exception cref="InvalidOperationException">A step of a kind the tool didn't declare: the site's fault.</exception>
    /// <exception cref="McpException"><c>usage</c> for more steps than the site allows (<see cref="OptiCliMcpOptions.MaxPlanSteps"/>).</exception>
    public void Validate(ContentPlan plan)
    {
        if (plan.Steps.FirstOrDefault(s => (kinds & s.Kind) == 0) is { } step)
        {
            throw new InvalidOperationException(
                $"The site's write tool {tool} planned a {step.KindName} step (step {step.Number}), which its [OptiCliWriteTool] Kinds ({kinds}) don't have: add {step.Kind} to Kinds, or plan no such step.");
        }
        if (plan.Count > maxSteps)
        {
            throw ToolErrors.Usage(
                $"This would take {plan.Count} steps; the site lets a tool take at most {maxSteps} in one run. Nothing was changed.",
                "Narrow the arguments (fewer items, a smaller part of the site) and run the dry run again; do the rest in further runs.");
        }
    }

    public CallToolResult DryRun(ContentPlan plan, JsonNode arguments)
    {
        Validate(plan);
        var checks = CheckAll(plan);
        var runnable = checks.All(c => c.Runnable);
        var result = new JsonObject
        {
            ["tool"] = tool,
            ["dryRun"] = true,
            ["valid"] = runnable,
        };
        if (runnable)
        {
            // Whole seconds, as the token carries it.
            var expires = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().Add(TokenLifetime).ToUnixTimeSeconds());
            result["planToken"] = _tokens.Protect(FormattableString.Invariant($"{expires.ToUnixTimeSeconds()}.{Hash(plan, arguments, checks)}"));
            result["planTokenExpires"] = expires;
        }
        result["steps"] = new JsonArray(checks.Select(c => (JsonNode)Report(plan, c)).ToArray());
        return Text(result, error: false);
    }

    public CallToolResult Run(ContentPlan plan, JsonNode arguments, string planToken, CancellationToken cancellationToken)
    {
        Validate(plan);
        var (expected, expires) = Unprotect(planToken);
        if (time.GetUtcNow() >= expires)
        {
            throw ToolErrors.From(new AgentError(AgentErrorCodes.Conflict,
                $"The planToken expired: a dry run's token works for {TokenLifetime.TotalMinutes:0} minutes. Nothing was saved.",
                "Run the tool with dryRun: true again, show the user the changes (the content may have changed since), and run it with the new planToken."));
        }

        // Every step checked again before the first is saved.
        var checks = CheckAll(plan);
        if (checks.FirstOrDefault(c => !c.Runnable) is { } refused)
        {
            var error = refused.Error ?? new AgentError(AgentErrorCodes.Validation, "The step's values don't pass the site's validation (the step's validation says why).");
            return Stopped(plan, checks.Select(c => Report(plan, c)).ToList(), completed: 0,
                $"Step {refused.Step.Number} of {plan.Count} ({refused.Step.KindName}) can't run now: {error.Message} Nothing was saved.",
                "Run the tool with dryRun: true again and show the user what it reports.",
                error.Code);
        }
        if (!string.Equals(expected, Hash(plan, arguments, checks), StringComparison.Ordinal))
        {
            throw ToolErrors.From(new AgentError(AgentErrorCodes.Conflict,
                "The plan isn't the one the dry run showed: the content changed since, or the arguments differ. Nothing was saved.",
                "Run the tool with dryRun: true again, show the user the new changes, and run it with the new planToken."));
        }

        var created = new Dictionary<int, ContentReference>();
        var reports = new List<JsonObject>();
        for (var i = 0; i < checks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = checks[i].Step;
            StepOutcome outcome;
            try
            {
                outcome = cms.Run(Resolve(plan, step, created), checks[i].Outcome!.Facts);
            }
            catch (Exception e) when (Error(e) is { } error)
            {
                audit?.PlanStep(editor, client, tool, step.Number, step.KindName, Target(step, created), "failed");
                reports.Add(Head(step, "failed", error));
                reports.AddRange(checks.Skip(i + 1).Select(c => Head(c.Step, "notRun")));
                var saved = i == 0 ? "No step was saved." : i == 1 ? "Step 1 was saved and stays saved." : $"Steps 1 to {i} were saved and stay saved.";
                return Stopped(plan, reports, completed: i,
                    $"Step {step.Number} of {plan.Count} ({step.KindName}) failed: {error.Message} {saved}",
                    i == 0
                        ? "Tell the user why it failed (the step's error has a hint)."
                        : "Tell the user which steps were saved (their editUrl opens the drafts in the CMS) and why the next one failed (the step's error has a hint). Nothing was undone.",
                    error.Code);
            }
            if (step is CreateStep && outcome.Content is { } made)
            {
                created[step.Number] = made.ToReferenceWithoutVersion();
            }
            audit?.PlanStep(editor, client, tool, step.Number, step.KindName, outcome.Content?.ToString() ?? Target(step, created), outcome.Status ?? "saved");
            var report = Head(step, outcome.Status ?? "saved");
            Merge(report, outcome.Report);
            reports.Add(report);
        }
        return Text(new JsonObject
        {
            ["tool"] = tool,
            ["dryRun"] = false,
            ["completed"] = checks.Count,
            ["total"] = checks.Count,
            ["steps"] = new JsonArray(reports.Select(r => (JsonNode)r).ToArray()),
        }, error: false);
    }

    /// <summary>The token's hash and when it expires.</summary>
    /// <exception cref="McpException"><c>usage</c> for a token this tool's dry run didn't give.</exception>
    private (string Hash, DateTimeOffset Expires) Unprotect(string planToken)
    {
        string payload;
        try
        {
            payload = _tokens.Unprotect(planToken);
        }
        catch (CryptographicException)
        {
            payload = "";
        }
        var dot = payload.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && long.TryParse(payload.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return (payload[(dot + 1)..], DateTimeOffset.FromUnixTimeSeconds(seconds));
        }
        throw ToolErrors.Usage("planToken isn't one this tool's dry run gave. Nothing was changed.",
            "Run the tool with dryRun: true, show the user the changes, then run it with the planToken that dry run returned.");
    }

    private List<Checked> CheckAll(ContentPlan plan)
    {
        var checks = new List<Checked>();
        // Trash steps whose own check passed: their content is the editor's to read.
        var trashed = new List<TrashStep>();
        foreach (var step in plan.Steps)
        {
            var (checkable, uncheckedValues) = Checkable(plan, step);
            try
            {
                var outcome = cms.Check(checkable);
                var below = BelowTrashed(trashed, checkable);
                if (step is TrashStep trash)
                {
                    trashed.Add(trash);
                }
                checks.Add(below is { } error
                    ? new Checked(step, null, uncheckedValues, error)
                    : new Checked(step, outcome, uncheckedValues, null));
            }
            catch (Exception e) when (Error(e) is { } error)
            {
                checks.Add(new Checked(step, null, uncheckedValues, error));
            }
        }
        return checks;
    }

    /// <summary>
    /// Whether <paramref name="step"/> uses content below content an earlier step trashes (the same content
    /// <see cref="ContentPlan"/> refuses as it is planned): it would act inside the recycle bin. Asked after the step's own
    /// check passed, and only of trash steps whose own check passed, so both are readable and naming them leaks nothing.
    /// </summary>
    /// <param name="trashed">The earlier trash steps whose own check passed.</param>
    /// <returns>The step's error; null when it doesn't.</returns>
    private AgentError? BelowTrashed(IReadOnlyList<TrashStep> trashed, PlanStep step)
    {
        if (trashed.Count == 0)
        {
            return null;
        }
        IEnumerable<ContentReference> uses = step switch
        {
            UpdateStep update => [update.Content],
            CreateStep create => [create.Parent.Existing!],
            MoveStep move => [move.Content, move.Destination.Existing!],
            TrashStep trash => [trash.Content],
            _ => [],
        };
        foreach (var link in uses)
        {
            var ancestors = cms.AncestorIds(link);
            if (trashed.FirstOrDefault(t => ancestors.Contains(t.Content.ID)) is { } earlier)
            {
                return new AgentError(AgentErrorCodes.Refused,
                    $"Step {earlier.Number} trashes content {earlier.Content.ID}, and content {link.ID}, which this step uses, is below it: no later step can use it.",
                    "The site's tool planned this; tell the user it can't run as planned. Nothing was changed.");
            }
        }
        return null;
    }

    /// <summary>What the assistant is told about a step that failed or was refused; null to let it propagate (cancellation, an MCP error of the SDK's).</summary>
    private AgentError? Error(Exception exception) => exception switch
    {
        OperationCanceledException => null,
        ToolErrors.ToolError error => JsonSerializer.Deserialize<AgentError>(error.Message, AgentJson.Options),
        McpException => null,
        _ => ToolErrors.ErrorOf(exception, logger, tool),
    };

    /// <summary>
    /// <paramref name="step"/> without <see cref="PlannedContent"/>, for its dry run: a planned parent or destination
    /// replaced by the existing content it is planned below, values and area items that refer to planned content left out.
    /// </summary>
    /// <returns>The step to check, and what was left out of it, as the result shows it.</returns>
    private static (PlanStep Step, JsonObject? Unchecked) Checkable(ContentPlan plan, PlanStep step)
    {
        switch (step)
        {
            case UpdateStep update:
            {
                var (properties, left) = Split(plan, update.Properties);
                var areaOps = update.AreaOps.Where(a => a.Block.Planned is null).ToList();
                var leftOps = update.AreaOps.Where(a => a.Block.Planned is not null).ToList();
                var notChecked = Unchecked(left, leftOps);
                return (update with { Properties = properties, AreaOps = areaOps }, notChecked);
            }
            case CreateStep create:
            {
                var (properties, left) = Split(plan, create.Properties);
                var checkable = create with { Properties = properties };
                if (create.Parent.Planned is { } parent)
                {
                    var (standIn, inAssets) = StandIn(plan, parent);
                    checkable = checkable with { Parent = new PlanTarget(standIn, null), InAssets = inAssets, PlannedParentType = parent.Type };
                }
                return (checkable, Unchecked(left, []));
            }
            case MoveStep move when move.Destination.Planned is { } destination:
            {
                var (standIn, inAssets) = StandIn(plan, destination);
                return (move with { Destination = new PlanTarget(standIn, null), PlannedDestinationType = destination.Type, DestinationInAssets = inAssets }, null);
            }
            default:
                return (step, null);
        }
    }

    /// <summary>The existing content <paramref name="planned"/> will be made below: its parent, or its planned parent's, and so on.</summary>
    /// <returns>That content, and whether it is planned in its "For this page" folder.</returns>
    private static (ContentReference StandIn, bool InAssets) StandIn(ContentPlan plan, PlannedContent planned)
    {
        var create = (CreateStep)plan.Steps[planned.Step - 1];
        while (create.Parent.Planned is { } parent)
        {
            create = (CreateStep)plan.Steps[parent.Step - 1];
        }
        return (create.Parent.Existing!, create.InAssets);
    }

    /// <summary>The values without planned content in them, and those with.</summary>
    private static (JsonObject Checkable, JsonObject Left) Split(ContentPlan plan, JsonObject properties)
    {
        var checkable = new JsonObject();
        var left = new JsonObject();
        foreach (var (name, value) in properties)
        {
            (PlanValues.Steps(value, plan).Any() ? left : checkable)[name] = value?.DeepClone();
        }
        return (checkable, left);
    }

    private static JsonObject? Unchecked(JsonObject values, IReadOnlyList<PlanAreaOp> areaOps)
    {
        if (values.Count == 0 && areaOps.Count == 0)
        {
            return null;
        }
        var result = new JsonObject();
        if (values.Count > 0)
        {
            result["properties"] = values;
        }
        if (areaOps.Count > 0)
        {
            result["areaOps"] = new JsonArray(areaOps.Select(a => (JsonNode)AreaOp(a, a.Block.ToString())).ToArray());
        }
        return result;
    }

    /// <summary><paramref name="step"/> with the content earlier steps made in place of its <see cref="PlannedContent"/>.</summary>
    private static PlanStep Resolve(ContentPlan plan, PlanStep step, IReadOnlyDictionary<int, ContentReference> created)
    {
        ContentReference Made(int number) => created.TryGetValue(number, out var link)
            ? link
            : throw new InvalidOperationException($"Step {number} hasn't made its content yet.");
        PlanTarget Target(PlanTarget target) => target.Planned is { } planned ? new PlanTarget(Made(planned.Step), null) : target;
        JsonObject Values(JsonObject values) => (JsonObject)PlanValues.Replace(values, plan, number => JsonValue.Create(Made(number).ToString()))!;
        return step switch
        {
            UpdateStep update => update with { Properties = Values(update.Properties), AreaOps = update.AreaOps.Select(a => a with { Block = Target(a.Block) }).ToList() },
            CreateStep create => create with { Parent = Target(create.Parent), Properties = Values(create.Properties) },
            MoveStep move => move with { Destination = Target(move.Destination) },
            _ => step,
        };
    }

    /// <summary>The content a step names, for the audit of a step that failed.</summary>
    private static string Target(PlanStep step, IReadOnlyDictionary<int, ContentReference> created) => step switch
    {
        UpdateStep update => update.Content.ToString(),
        CreateStep create => "new below " + (create.Parent.Planned is { } planned && created.TryGetValue(planned.Step, out var made) ? made.ToString() : create.Parent.ToString()),
        MoveStep move => move.Content.ToString(),
        TrashStep trash => trash.Content.ToString(),
        _ => "-",
    };

    /// <summary>A step of the dry run: what its check reported, what it left unchecked, or why it would be refused.</summary>
    private static JsonObject Report(ContentPlan plan, Checked check)
    {
        var report = new JsonObject { ["step"] = check.Step.Number, ["kind"] = check.Step.KindName };
        switch (check.Step)
        {
            case CreateStep create:
                report["type"] = create.Type;
                report["name"] = create.Name;
                report[create.InAssets ? "forContent" : "parent"] = create.Parent.ToString();
                break;
            case MoveStep move:
                report["destination"] = move.Destination.ToString();
                break;
        }
        if (check.Outcome is { } outcome)
        {
            Merge(report, outcome.Report);
        }
        // What the operation's dry run reported for a stand-in is the planned content's.
        if (check.Step is CreateStep { Parent.Planned: { } parent, InAssets: var inAssets })
        {
            report[inAssets ? "forContent" : "parent"] = parent.ToString();
        }
        if (check.Step is MoveStep { Destination.Planned: { } destination })
        {
            report["parent"] = destination.ToString();
        }
        if (check.Unchecked is { } left)
        {
            report["unchecked"] = PlanValues.Replace(left, plan, number => JsonValue.Create($"step {number}"));
            report["uncheckedNote"] = "Refers to content an earlier step creates: checked when this step runs.";
        }
        if (check.Error is { } error)
        {
            report["error"] = JsonSerializer.SerializeToNode(error, AgentJson.Options);
        }
        return report;
    }

    private static JsonObject Head(PlanStep step, string status, AgentError? error = null)
    {
        var head = new JsonObject { ["step"] = step.Number, ["kind"] = step.KindName, ["status"] = status };
        if (error is not null)
        {
            head["error"] = JsonSerializer.SerializeToNode(error, AgentJson.Options);
        }
        return head;
    }

    /// <summary>Adds <paramref name="from"/>'s fields to <paramref name="into"/>, but its <c>dryRun</c> (the plan's result says it once).</summary>
    private static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var (name, value) in from)
        {
            if (name != "dryRun" && !into.ContainsKey(name))
            {
                into[name] = value?.DeepClone();
            }
        }
    }

    private CallToolResult Stopped(ContentPlan plan, IReadOnlyList<JsonObject> steps, int completed, string message, string hint, string code)
    {
        var result = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["hint"] = hint,
            ["tool"] = tool,
            ["completed"] = completed,
            ["total"] = plan.Count,
            ["steps"] = new JsonArray(steps.Select(s => (JsonNode)s).ToArray()),
        };
        return Text(result, error: true);
    }

    private static CallToolResult Text(JsonObject result, bool error) =>
        new() { IsError = error ? true : null, Content = [new TextContentBlock { Text = result.ToJsonString(AgentJson.Options) }] };

    /// <summary>
    /// The plan as the token covers it: the tool, the editor, the arguments as the tool read them, and every step with what
    /// its check found. Planned content is named by its step, never by the plan's marker.
    /// </summary>
    private string Hash(ContentPlan plan, JsonNode arguments, IReadOnlyList<Checked> checks)
    {
        var steps = new JsonArray();
        foreach (var check in checks)
        {
            var canonical = Canonical(plan, check.Step);
            canonical["facts"] = check.Outcome?.Facts?.DeepClone();
            steps.Add(canonical);
        }
        var data = new JsonObject
        {
            ["tool"] = tool,
            ["editor"] = editor,
            ["arguments"] = arguments.DeepClone(),
            ["steps"] = steps,
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToJsonString())));
    }

    private static JsonObject Canonical(ContentPlan plan, PlanStep step)
    {
        JsonNode Planned(int number) => new JsonObject { ["$step"] = number };
        JsonNode? Target(PlanTarget target) => target.Planned is { } planned ? Planned(planned.Step) : target.Existing!.ToString();
        JsonNode? Values(JsonObject values) => PlanValues.Replace(values, plan, Planned);
        var json = new JsonObject { ["kind"] = step.KindName };
        switch (step)
        {
            case UpdateStep update:
                json["content"] = update.Content.ToString();
                json["language"] = update.Language;
                json["name"] = update.Name;
                json["properties"] = Values(update.Properties);
                json["areaOps"] = new JsonArray(update.AreaOps.Select(a => (JsonNode)AreaOp(a, Target(a.Block))).ToArray());
                break;
            case CreateStep create:
                json["parent"] = Target(create.Parent);
                json["inAssets"] = create.InAssets;
                json["type"] = create.Type;
                json["name"] = create.Name;
                json["language"] = create.Language;
                json["properties"] = Values(create.Properties);
                break;
            case MoveStep move:
                json["content"] = move.Content.ToString();
                json["destination"] = Target(move.Destination);
                break;
            case TrashStep trash:
                json["content"] = trash.Content.ToString();
                break;
        }
        return json;
    }

    private static JsonObject AreaOp(PlanAreaOp op, JsonNode? block) => new()
    {
        ["op"] = op.Op,
        ["property"] = op.Property,
        ["ref"] = block,
        ["at"] = op.At,
        ["displayOption"] = op.DisplayOption,
        ["ifMissing"] = op.IfMissing,
    };
}
