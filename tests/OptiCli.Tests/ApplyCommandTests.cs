using OptiCli.Commands;
using OptiCli.Core.Writes;

namespace OptiCli.Tests;

/// <summary><c>apply</c>: one draft per content and language, and <c>--version-per-step</c>.</summary>
[Collection(ConsoleCollection.Name)]
public class ApplyCommandTests
{
    [Fact]
    public async Task Apply_documents_the_one_draft_and_its_opt_out()
    {
        var run = await Opticli.RunAsync("apply", "--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("--version-per-step", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("so a plan leaves one draft per content and language", string.Join(' ', run.Stdout.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_dry_run_says_the_real_run_saves_into_one_draft_unless_a_version_per_step()
    {
        PlanStepResult Step(int index, string op) => new(index, op, null, PlanStepStatus.Valid);
        var plan = new PlanRun(true, [Step(0, "set"), Step(1, "area"), Step(2, "publish")], new Dictionary<string, string>());
        const string Note = "saves into that draft (one draft per content and language; --version-per-step: a version each).";

        Assert.EndsWith(Note, ApplyCommand.Coverage(plan), StringComparison.Ordinal);
        Assert.DoesNotContain("draft", ApplyCommand.Coverage(plan, versionPerStep: true), StringComparison.Ordinal);
        Assert.DoesNotContain("draft", ApplyCommand.Coverage(plan with { Operations = [Step(0, "set"), Step(1, "publish")] }), StringComparison.Ordinal);
        Assert.EndsWith(Note, ApplyCommand.Coverage(plan with { Operations = [Step(0, "create"), Step(1, "set")] }), StringComparison.Ordinal);
        Assert.EndsWith(Note, ApplyCommand.Coverage(plan with { Operations = [Step(0, "block"), Step(1, "move"), Step(2, "area")] }), StringComparison.Ordinal);
        Assert.DoesNotContain("draft", ApplyCommand.Coverage(plan with { Operations = [Step(0, "publish"), Step(1, "set")] }), StringComparison.Ordinal);
    }
}
