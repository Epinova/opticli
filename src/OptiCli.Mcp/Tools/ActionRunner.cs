using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OptiCli.Protocol;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// The dry run and the real run of an action tool's call (<see cref="IOptiCliActionTool{TArgs}"/>): checks the preview
/// against what the tool declares and the site allows, gives the token, and runs the tool's change once per token,
/// recording each run (<see cref="IActionRunStore"/>) and auditing each target as the tool reports it done.
/// </summary>
/// <remarks>
/// <para>
/// The token is a hash, protected with the site's Data Protection keys (<see cref="PlanTokens"/>), of the tool, the
/// editor, the arguments as the tool read them, and every target's kind, id, label, effect, changes and version. The
/// real run previews again and runs only when that hash is the same.
/// </para>
/// <para>
/// A token runs once, by its record, whether or not its targets have versions: the same token again answers with the
/// recorded result, or, for a run that started and never recorded an end (still running, or the site stopped during
/// it), with a <c>conflict</c> that says so. A call cancelled during the run records an end too, with the target it was
/// on as <c>unknown</c>. The record is looked up before the token's expiry is checked, and kept
/// <see cref="RecordKeptAfterToken"/> past it, so a retry at the edge of the token's life still gets the run's result.
/// </para>
/// </remarks>
internal sealed class ActionRunner(
    ActionToolType type, OptiCliMcpOptions site, string editor, string client, IReadOnlyCollection<string> scopes, IActionRunStore runs, McpAudit? audit,
    ILogger logger, IDataProtectionProvider protection, TimeProvider time)
{
    /// <summary>The Data Protection purpose of action tokens, with the tool's name under it.</summary>
    internal const string TokenPurpose = "OptiCli.Mcp.ActionToken";

    /// <summary>How long a run's record is kept after its token expired.</summary>
    internal static readonly TimeSpan RecordKeptAfterToken = TimeSpan.FromMinutes(30);

    private readonly IDataProtector _tokens = protection.CreateProtector(TokenPurpose, type.Name);

    /// <summary>
    /// What the tool may preview, and what the site allows: every target's effect one the tool declares, at most
    /// <see cref="OptiCliMcpOptions.MaxPlanSteps"/> targets, and the publish gate for a target that changes what visitors
    /// see.
    /// </summary>
    /// <exception cref="InvalidOperationException">A target of an effect the tool didn't declare: the site's fault.</exception>
    /// <exception cref="McpException"><c>usage</c> for too many targets; <c>refused</c> by the publish gate.</exception>
    public void Check(ActionPreview preview)
    {
        if (preview.Targets.FirstOrDefault(t => (type.Effects & t.Effect) == 0) is { } target)
        {
            throw new InvalidOperationException(
                $"The site's action tool {type.Name} previewed target {target.Number} ({target.Kind}) with the effect {target.Effect}, which its [OptiCliActionTool] Effects ({type.Effects}) don't have: add {target.Effect} to Effects, or preview no such target.");
        }
        if (preview.Targets.Count > site.MaxPlanSteps)
        {
            throw ToolErrors.Usage(
                $"This would change {preview.Targets.Count} things; the site lets a tool change at most {site.MaxPlanSteps} in one run. Nothing was changed.",
                "Narrow the arguments (fewer items) and run the dry run again; do the rest in further runs.");
        }
        if (preview.Targets.Any(t => t.Effect == ActionEffects.Live))
        {
            ToolGates.Publishing(scopes, site, ActionToolType.PublishingOffHint);
        }
    }

    public CallToolResult DryRun(ActionPreview preview, JsonNode arguments)
    {
        Check(preview);
        var result = new JsonObject
        {
            ["tool"] = type.Name,
            ["dryRun"] = true,
            ["valid"] = preview.Valid,
        };
        if (preview.Valid)
        {
            var (token, expires) = PlanTokens.Issue(_tokens, time, Hash(preview, arguments));
            result["planToken"] = token;
            result["planTokenExpires"] = expires;
        }
        result["targets"] = new JsonArray(preview.Targets.Select(t => (JsonNode)Report(t)).ToArray());
        if (preview.Notes.Count > 0)
        {
            result["notes"] = new JsonArray(preview.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        }
        return Text(result, error: false);
    }

    /// <param name="preview">Previews again, on the instance <paramref name="run"/> runs on.</param>
    /// <param name="run">The tool's <see cref="IOptiCliActionTool{TArgs}.RunAsync"/>.</param>
    public async Task<CallToolResult> RunAsync(
        JsonNode arguments, string planToken, Func<Task<ActionPreview>> preview, Func<ActionPreview, ActionRun, Task> run, CancellationToken cancellationToken)
    {
        var (expected, expires) = PlanTokens.Read(_tokens, planToken) ?? throw NotThisTools();
        var key = RunKey(planToken);
        var argumentsHash = Sha256(arguments.ToJsonString());
        if (runs.Find(key) is { } ran)
        {
            return Repeat(ran, argumentsHash);
        }
        if (time.GetUtcNow() >= expires)
        {
            throw ToolErrors.From(new AgentError(AgentErrorCodes.Conflict,
                $"The planToken expired: a dry run's token works for {PlanTokens.Lifetime.TotalMinutes:0} minutes. Nothing was changed.",
                "Run the tool with dryRun: true again, show the user the changes (they may have changed since), and run it with the new planToken."));
        }

        var current = await preview();
        Check(current);
        if (!current.Valid)
        {
            var target = current.Targets.First(t => t.Problems.Count > 0);
            return Stopped(current.Targets.Select(Report).ToList(), completed: 0,
                $"Target {target.Number} of {current.Targets.Count} ({target.Kind}) can't run now: {target.Problems[0].Message} Nothing was changed.",
                "Run the tool with dryRun: true again and show the user what it reports.",
                AgentErrorCodes.Validation);
        }
        if (!string.Equals(expected, Hash(current, arguments), StringComparison.Ordinal))
        {
            throw ToolErrors.From(new AgentError(AgentErrorCodes.Conflict,
                "The changes aren't the ones the dry run showed: something changed since, or the arguments differ. Nothing was changed.",
                "Run the tool with dryRun: true again, show the user the new changes, and run it with the new planToken."));
        }

        // Not once it is recorded as started: a run cancelled before it ran would look as if it might have.
        cancellationToken.ThrowIfCancellationRequested();
        var started = time.GetUtcNow();
        if (runs.TryStart(new ActionRunRecord(key, editor, argumentsHash, started, expires + RecordKeptAfterToken), started) is { } raced)
        {
            return Repeat(raced, argumentsHash);
        }
        var actionRun = new ActionRun(current, key, (target, done) =>
            audit?.ActionTarget(editor, client, type.Name, target.Number, EffectName(target.Effect), AuditTarget(target.Kind, done.Id), done.Outcome));
        CallToolResult result;
        try
        {
            // An empty preview runs nothing, as an empty plan does.
            if (current.Targets.Count > 0)
            {
                await run(current, actionRun);
            }
            actionRun.End();
            result = Finished(current, actionRun);
        }
        catch (Exception e) when (Error(e) is { } error)
        {
            actionRun.End();
            result = Failed(current, actionRun, error);
        }
        catch (Exception e)
        {
            // Cancelled, or an MCP error that goes to the client as it is: the run still gets its end, so the same token
            // again says which targets were done rather than that the run may still be running.
            actionRun.End();
            var stopped = Unknown(current, actionRun, e is OperationCanceledException);
            runs.Finish(key, time.GetUtcNow(), Text(stopped), stopped.IsError == true);
            throw;
        }
        runs.Finish(key, time.GetUtcNow(), Text(result), result.IsError == true);
        return result;
    }

    /// <summary>
    /// The key of a token's run (<see cref="ActionRun.Key"/>, the record's key): a SHA-256 of the token's bytes rather than
    /// of its text, so the same token written another way (with whitespace, which base64 decoding skips) is the same run.
    /// </summary>
    /// <param name="planToken">A token <see cref="PlanTokens.Read"/> could read.</param>
    internal static string RunKey(string planToken) => Convert.ToHexString(SHA256.HashData(WebEncoders.Base64UrlDecode(planToken)));

    /// <summary>The result of a run that returned: every target done, or the site's fault for those it didn't report.</summary>
    private CallToolResult Finished(ActionPreview preview, ActionRun run)
    {
        var missing = preview.Targets.Where(t => !run.Outcomes.ContainsKey(t)).ToList();
        var reports = preview.Targets.Select(t => Done(t, run)).ToList();
        if (missing.Count == 0)
        {
            return Text(new JsonObject
            {
                ["tool"] = type.Name,
                ["dryRun"] = false,
                ["completed"] = preview.Targets.Count,
                ["total"] = preview.Targets.Count,
                ["targets"] = new JsonArray(reports.Select(r => (JsonNode)r).ToArray()),
            }, error: false);
        }
        foreach (var target in missing)
        {
            audit?.ActionTarget(editor, client, type.Name, target.Number, EffectName(target.Effect), AuditTarget(target.Kind, target.Id), "notReported");
        }
        var numbers = string.Join(", ", missing.Select(t => t.Number));
        logger.LogError("The site's action tool {Tool} ended its run without reporting target(s) {Targets} done or failed: report each target with ActionRun.Done, or throw for the one that failed.", type.Name, numbers);
        return Stopped(reports, completed: preview.Targets.Count - missing.Count,
            $"The site's tool ended its run without saying what it did with target(s) {numbers}: whether they were changed isn't known.",
            "Tell the user which targets were done, and to check the others where they work on them (their editUrl in the dry run). The details are in the site's log.",
            AgentErrorCodes.Internal);
    }

    /// <summary>The result of a run the tool stopped by throwing: the first target not done is the one that failed.</summary>
    private CallToolResult Failed(ActionPreview preview, ActionRun run, AgentError error)
    {
        var failed = preview.Targets.FirstOrDefault(t => !run.Outcomes.ContainsKey(t));
        var reports = new List<JsonObject>();
        foreach (var target in preview.Targets)
        {
            if (run.Outcomes.ContainsKey(target))
            {
                reports.Add(Done(target, run));
            }
            else if (ReferenceEquals(target, failed))
            {
                audit?.ActionTarget(editor, client, type.Name, target.Number, EffectName(target.Effect), AuditTarget(target.Kind, target.Id), "failed");
                reports.Add(Head(target, "failed", target.Id, error));
            }
            else
            {
                reports.Add(Head(target, "notRun", target.Id));
            }
        }
        var completed = run.Outcomes.Count;
        if (failed is null)
        {
            return Stopped(reports, completed,
                $"Every target was done, then the site's tool failed: {error.Message}",
                "Tell the user every change was made, and what failed after them (the error's hint, if it has one).",
                error.Code);
        }
        var done = completed == 0 ? "Nothing was changed." : completed == 1 ? "One target was done and stays done." : $"{completed} targets were done and stay done.";
        return Stopped(reports, completed,
            $"Target {failed.Number} of {preview.Targets.Count} ({failed.Kind}) failed: {error.Message} {done}",
            completed == 0
                ? "Tell the user why it failed" + (error.Hint is null ? "." : $": {error.Hint}")
                : "Tell the user which targets were done (their editUrl opens them) and why the next one failed" + (error.Hint is null ? "." : $": {error.Hint}") + " Nothing was undone.",
            error.Code);
    }

    /// <summary>
    /// The result of a run that stopped without an answer for the target it was on (the call was cancelled, or the tool
    /// threw an MCP error of its own): that target may or may not have been changed.
    /// </summary>
    private CallToolResult Unknown(ActionPreview preview, ActionRun run, bool cancelled)
    {
        var stopped = preview.Targets.FirstOrDefault(t => !run.Outcomes.ContainsKey(t));
        if (stopped is null)
        {
            return Finished(preview, run);
        }
        var reports = new List<JsonObject>();
        foreach (var target in preview.Targets)
        {
            if (run.Outcomes.ContainsKey(target))
            {
                reports.Add(Done(target, run));
            }
            else if (ReferenceEquals(target, stopped))
            {
                audit?.ActionTarget(editor, client, type.Name, target.Number, EffectName(target.Effect), AuditTarget(target.Kind, target.Id), "unknown");
                reports.Add(Head(target, "unknown", target.Id));
            }
            else
            {
                reports.Add(Head(target, "notRun", target.Id));
            }
        }
        var completed = run.Outcomes.Count;
        var done = completed == 0 ? "No target before it was done." : completed == 1 ? "One target was done and stays done." : $"{completed} targets were done and stay done.";
        return Stopped(reports, completed,
            $"The run stopped at target {stopped.Number} of {preview.Targets.Count} ({stopped.Kind}) because {(cancelled ? "the call was cancelled" : "the site's tool sent an error of its own")}: whether that target was changed isn't known. {done}",
            $"Tell the user which targets were done (their editUrl opens them), and to check target {stopped.Number} where they work on it (its editUrl in the dry run) before you run the dry run again. Nothing was undone.",
            AgentErrorCodes.Conflict);
    }

    /// <summary>The answer to a token that ran before: its result again, or why there is none.</summary>
    private CallToolResult Repeat(ActionRunRecord ran, string argumentsHash)
    {
        if (!string.Equals(ran.Editor, editor, StringComparison.Ordinal))
        {
            throw NotThisTools();
        }
        if (!string.Equals(ran.Arguments, argumentsHash, StringComparison.Ordinal))
        {
            throw ToolErrors.From(new AgentError(AgentErrorCodes.Conflict,
                "This planToken ran already, with other arguments. Nothing was run again.",
                "Run the tool with dryRun: true with the arguments the user wants, show them the changes, and run it with the new planToken."));
        }
        if (ran.Finished is null || ran.Result is null)
        {
            throw ToolErrors.From(new AgentError(AgentErrorCodes.Conflict,
                FormattableString.Invariant($"This planToken's run started at {ran.Started.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC and hasn't said how it ended: it may still be running, or the site stopped during it. Nothing was run again."),
                "Tell the user. They check what was changed where they work on it (the targets' editUrl in the dry run) before you run the dry run again."));
        }
        var result = JsonNode.Parse(ran.Result)!.AsObject();
        result["repeated"] = true;
        result["ranAt"] = ran.Started;
        result["repeatNote"] = "This planToken ran before: this is that run's result. Nothing was run again.";
        return Text(result, ran.Failed);
    }

    private static McpException NotThisTools() => ToolErrors.Usage("planToken isn't one this tool's dry run gave. Nothing was changed.",
        "Run the tool with dryRun: true, show the user the changes, then run it with the planToken that dry run returned.");

    /// <summary>What the assistant is told about a target that failed; null to let it propagate (cancellation, an MCP error of the SDK's).</summary>
    private AgentError? Error(Exception exception) => exception switch
    {
        OperationCanceledException => null,
        ToolErrors.ToolError error => JsonSerializer.Deserialize<AgentError>(error.Message, AgentJson.Options),
        McpException => null,
        _ => ToolErrors.ErrorOf(exception, logger, type.Name),
    };

    /// <summary>A target of the dry run: what it is and what changes on it, why it can't run, where the user sees it.</summary>
    private static JsonObject Report(ActionTarget target)
    {
        var report = new JsonObject
        {
            ["target"] = target.Number,
            ["kind"] = target.Kind,
            ["id"] = target.Id,
            ["label"] = target.Label,
            ["effect"] = EffectName(target.Effect),
        };
        if (target.Id is null)
        {
            report.Remove("id");
            report["new"] = true;
        }
        if (target.CurrentVersion is { } version)
        {
            report["version"] = version;
        }
        if (target.Changes.Count > 0)
        {
            report["changes"] = new JsonArray(target.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["field"] = c.Field,
                ["before"] = c.Before?.DeepClone(),
                ["after"] = c.After?.DeepClone(),
            }).ToArray());
        }
        if (target.Problems.Count > 0)
        {
            report["problems"] = new JsonArray(target.Problems.Select(p => (JsonNode)Problem(p)).ToArray());
        }
        Links(report, target.Url, target.EditUrl);
        return report;
    }

    private static JsonObject Problem(ActionProblem problem)
    {
        var json = new JsonObject { ["message"] = problem.Message };
        if (problem.Field is { } field)
        {
            json["field"] = field;
        }
        return json;
    }

    private static JsonObject Done(ActionTarget target, ActionRun run)
    {
        if (!run.Outcomes.TryGetValue(target, out var done))
        {
            return Head(target, "notReported", target.Id);
        }
        var report = Head(target, done.Outcome, done.Id);
        Links(report, done.Url, done.EditUrl);
        return report;
    }

    private static JsonObject Head(ActionTarget target, string status, string? id, AgentError? error = null)
    {
        var head = new JsonObject { ["target"] = target.Number, ["kind"] = target.Kind };
        if (id is not null)
        {
            head["id"] = id;
        }
        head["label"] = target.Label;
        head["status"] = status;
        if (error is not null)
        {
            head["error"] = JsonSerializer.SerializeToNode(error, AgentJson.Options);
        }
        return head;
    }

    private static void Links(JsonObject report, string? url, string? editUrl)
    {
        if (url is not null)
        {
            report["url"] = url;
        }
        if (editUrl is not null)
        {
            report["editUrl"] = editUrl;
        }
    }

    private CallToolResult Stopped(IReadOnlyList<JsonObject> targets, int completed, string message, string hint, string code) =>
        Text(new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["hint"] = hint,
            ["tool"] = type.Name,
            ["completed"] = completed,
            ["total"] = targets.Count,
            ["targets"] = new JsonArray(targets.Select(t => (JsonNode)t).ToArray()),
        }, error: true);

    private static CallToolResult Text(JsonObject result, bool error) =>
        new() { IsError = error ? true : null, Content = [new TextContentBlock { Text = result.ToJsonString(AgentJson.Options) }] };

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary><c>change</c>, <c>live</c> or <c>delete</c>.</summary>
    internal static string EffectName(ActionEffects effect) => effect.ToString().ToLowerInvariant();

    /// <summary>A target as the audit names it: <c>kind:id</c>, <c>kind:new</c> for one not made yet. Never its label.</summary>
    internal static string AuditTarget(string kind, string? id) => $"{kind}:{id ?? "new"}";

    /// <summary>
    /// The preview as the token covers it: the tool, the editor, the arguments as the tool read them, and every target's
    /// kind, id, label, effect, version and changes. Notes, links and problems aren't in it (problems give no token).
    /// </summary>
    private string Hash(ActionPreview preview, JsonNode arguments)
    {
        var data = new JsonObject
        {
            ["tool"] = type.Name,
            ["editor"] = editor,
            ["arguments"] = arguments.DeepClone(),
            ["targets"] = new JsonArray(preview.Targets.Select(t => (JsonNode)new JsonObject
            {
                ["kind"] = t.Kind,
                ["id"] = t.Id,
                ["label"] = t.Label,
                ["effect"] = EffectName(t.Effect),
                ["version"] = t.CurrentVersion,
                ["changes"] = new JsonArray(t.Changes.Select(c => (JsonNode)new JsonObject
                {
                    ["field"] = c.Field,
                    ["before"] = c.Before?.DeepClone(),
                    ["after"] = c.After?.DeepClone(),
                }).ToArray()),
            }).ToArray()),
        };
        return Sha256(data.ToJsonString());
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
