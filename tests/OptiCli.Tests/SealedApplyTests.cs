using OptiCli.Cli;
using OptiCli.Core;
using OptiCli.Core.Configuration;
using OptiCli.Core.Discovery;
using OptiCli.Core.Seals;

namespace OptiCli.Tests;

/// <summary>
/// The sealed apply at the command line: sealing (<c>apply --dry-run --seal-for</c>) and the user's run (<c>apply --db
/// &lt;prod&gt; --seal &lt;id&gt;</c>), every refusal before production is read, with a stand-in for the person at the terminal
/// (<see cref="SealedApply.Terminal"/>). Seals are made here as opticli makes them, since sealing needs the databases; no
/// database is connected to, except the last step of the confirmation test, which fails to.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SealedApplyTests : IDisposable
{
    private const string ProdDatabase = "alloy-edge-seal-prod";
    private const string Prod = $"Server=localhost,1433;Database={ProdDatabase};User Id=reader;Password=hunter2-read;TrustServerCertificate=True;Connect Timeout=2";
    private const string Page = "61104228-962d-4dd2-925c-b5b12e2373ee";

    private readonly RolesSite _site = new();
    private readonly FakeTerminal _terminal = new();

    public SealedApplyTests()
    {
        SealedApply.Terminal = _terminal;
        _site.WriteProjectFile("bin/Debug/net8.0/Web.dll", "the site's build");
        WritePlan($$$"""{"operations": [{"op": "set", "ref": "{{{Page}}}", "properties": {"TeaserText": "@texts/teaser.txt"}, "publish": true}]}""");
        _site.WriteProjectFile("plans/texts/teaser.txt", "Sealed");
    }

    public void Dispose()
    {
        SealedApply.Terminal = new ConsoleTerminal();
        _site.Dispose();
    }

    private string PlanFile => Path.Combine(_site.ProjectDirectory, "plans", "plan.json");

    private void WritePlan(string json) => _site.WriteProjectFile("plans/plan.json", json);

    private Task<Run> RunSealed(string seal, params string[] more) => _site.RunAsync(["apply", PlanFile, "--db", "prod", "--seal", seal, .. more]);

    [Fact]
    public async Task Without_a_terminal_it_is_refused_with_the_command_to_hand_over()
    {
        await AddProd();
        var seal = await SealAsync();
        _terminal.Interactive = false;

        var run = await RunSealed(seal.Id);

        AssertError(run, 3, "refused", SealedApply.TerminalReason);
        var command = run.Error().GetProperty("details").GetProperty("userCommand").GetString()!;
        Assert.Contains($"apply {PlanFile} --db prod --seal {seal.Id}", command, StringComparison.Ordinal);
        Assert.False(Used(seal.Id));
    }

    [Theory]
    [InlineData("--publish")]
    [InlineData("--update-existing")]
    [InlineData("--dry-run")]
    [InlineData("--allow-outside")]
    [InlineData("--request-approval")]
    [InlineData("--accept-drift", "a1b2c3d4e5f6")]
    [InlineData("--seal-for", "prod")]
    [InlineData("--connection", Prod)]
    [InlineData("--profile", "Prod")]
    public async Task The_run_takes_no_other_option(params string[] option)
    {
        await AddProd();
        var seal = await SealAsync();

        var run = await RunSealed(seal.Id, option);

        AssertError(run, 1, "usage");
        Assert.Contains("--seal takes no", run.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_needs_db_naming_production()
    {
        await AddProd();
        var seal = await SealAsync();

        AssertError(await _site.RunAsync("apply", PlanFile, "--seal", seal.Id), 1, "usage");
        AssertError(await _site.RunAsync("apply", PlanFile, "--db", RolesSite.DevDatabase, "--seal", seal.Id), 3, "refused", SealCheck.TargetReason);
    }

    [Fact]
    public async Task A_seal_that_isnt_one_was_edited_or_was_used_is_refused()
    {
        await AddProd();
        var seal = await SealAsync();

        AssertError(await RunSealed("../config"), 1, "usage");
        AssertError(await RunSealed("0123456789abcdef"), 2, "not_found");

        var file = Store().PathOf(seal.Id);
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"publish\": false", "\"publish\": true", StringComparison.Ordinal));
        AssertError(await RunSealed(seal.Id), 3, "refused", SealStore.TamperedReason);

        var used = await SealAsync();
        Store().Consume(used.Id);
        AssertError(await RunSealed(used.Id), 3, "refused", SealStore.UsedReason);
    }

    [Fact]
    public async Task A_seal_for_another_database_or_project_is_refused()
    {
        await AddProd();
        Assert.Equal(0, (await _site.RunAsync("db", "add", "prod2", "--role", "prod", "--connection", Prod.Replace(ProdDatabase, ProdDatabase + "2", StringComparison.Ordinal))).ExitCode);
        var seal = await SealAsync();
        var elsewhere = await SealAsync(s => s with { Project = s.Project + "-other" });

        AssertError(await _site.RunAsync("apply", PlanFile, "--db", "prod2", "--seal", seal.Id), 3, "refused", SealCheck.TargetReason);
        AssertError(await RunSealed(elsewhere.Id), 3, "refused", SealCheck.ProjectReason);
    }

    [Fact]
    public async Task An_old_seal_a_changed_plan_file_or_build_needs_a_new_seal()
    {
        await AddProd();
        var expired = await SealAsync(s => s with { Created = DateTimeOffset.UtcNow - Seal.MaxAge - TimeSpan.FromMinutes(1) });
        AssertError(await RunSealed(expired.Id), 5, "conflict", SealCheck.ExpiredReason);

        var planEdited = await SealAsync();
        WritePlan(File.ReadAllText(PlanFile).Replace("\"publish\": true", "\"publish\": false", StringComparison.Ordinal));
        AssertError(await RunSealed(planEdited.Id), 5, "conflict", SealCheck.PlanChangedReason);

        var fileSwapped = await SealAsync();
        _site.WriteProjectFile("plans/texts/teaser.txt", "Swapped after the seal");
        AssertError(await RunSealed(fileSwapped.Id), 5, "conflict", SealCheck.PlanChangedReason);

        var rebuilt = await SealAsync();
        _site.WriteProjectFile("bin/Debug/net8.0/Web.dll", "code changed after the seal");
        AssertError(await RunSealed(rebuilt.Id), 5, "conflict", SealCheck.BuildChangedReason);
        Assert.False(Used(rebuilt.Id));
    }

    [Fact]
    public async Task A_plan_that_does_more_than_a_sealed_apply_does_is_refused_when_sealing_and_when_run()
    {
        await AddProd();
        WritePlan($$$"""{"operations": [{"op": "delete", "ref": "{{{Page}}}"}, {"op": "set", "ref": "10", "properties": {"Heading": "x"}}]}""");
        var forged = await SealAsync();

        var sealing = await _site.RunAsync("apply", PlanFile, "--dry-run", "--seal-for", "prod");
        var running = await RunSealed(forged.Id);

        AssertError(sealing, 3, "refused", SealedPlan.Reason);
        AssertError(running, 3, "refused", SealedPlan.Reason);
        Assert.Equal(2, sealing.Error().GetProperty("details").GetProperty("problems").GetArrayLength());
    }

    [Fact]
    public async Task The_user_sees_what_the_plan_does_and_must_type_the_databases_name()
    {
        await AddProd();
        WritePlan($$"""{"operations": [{"op": "create", "id": "x", "guid": "71f80142-5378-5061-9f34-b49bc3640495", "parent": "{{Page}}", "type": "Std\u001b[2JPage", "name": "News", "publish": true}]}""");
        var seal = await SealAsync();
        _terminal.Lines.Enqueue("cms-prod");

        var wrong = await RunSealed(seal.Id);

        AssertError(wrong, 1, "usage");
        Assert.Contains("Not confirmed", wrong.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("PRODUCTION", _terminal.Output, StringComparison.Ordinal);
        Assert.Contains($"Type the database's name, '{ProdDatabase}'", _terminal.Output, StringComparison.Ordinal);
        Assert.Contains("1 create", _terminal.Output, StringComparison.Ordinal);
        // Text from the plan can't move the cursor or clear the screen before the user confirms.
        Assert.DoesNotContain('\u001b', _terminal.Output);
        Assert.Contains("Std?[2JPage", _terminal.Output, StringComparison.Ordinal);
        Assert.False(Used(seal.Id));

        // Confirmed: production is read next (here it can't be), and the seal is still unused.
        _terminal.Lines.Enqueue(ProdDatabase);
        var confirmed = await RunSealed(seal.Id);

        Assert.True(confirmed.ExitCode == 4, confirmed.ToString());
        Assert.DoesNotContain("Password", _terminal.Output, StringComparison.Ordinal);
        Assert.False(Used(seal.Id));
    }

    [Fact]
    public void A_hint_about_serve_points_at_the_runs_own_site_instead()
    {
        var hint = SealedApply.SiteHint("Check it with `opticli serve --status` and `opticli serve --logs`; start it with `opticli serve`.", "/state/sealed/x.log");

        Assert.DoesNotContain("opticli serve", hint, StringComparison.Ordinal);
        Assert.Contains("/state/sealed/x.log", hint, StringComparison.Ordinal);
        Assert.Equal("Seal again.", SealedApply.SiteHint("Seal again.", "/state/sealed/x.log"));
        Assert.Contains("edit UI", SealedApply.PartialHint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--seal-for", "prod" }, 1, "usage", null)]
    [InlineData(new[] { "--seal-for", "prod", "--dry-run", "--accept-drift", "a1b2c3d4e5f6" }, 1, "usage", null)]
    [InlineData(new[] { "--seal-for", "prod", "--dry-run", "--allow-outside" }, 1, "usage", null)]
    [InlineData(new[] { "--seal-for", RolesSite.DevDatabase, "--dry-run" }, 3, "refused", "sealTarget")]
    public async Task Sealing_needs_a_dry_run_for_production_and_nothing_that_weakens_it(string[] options, int exit, string code, string? reason)
    {
        await AddProd();

        AssertError(await _site.RunAsync(["apply", PlanFile, .. options]), exit, code, reason);
    }

    [Fact]
    public async Task Sealing_checks_against_a_local_database_only_and_a_plain_apply_to_production_points_at_sealing()
    {
        await AddProd();
        var dev = (await _site.RunAsync("db", "list")).Json().GetProperty("data").GetProperty("choices").EnumerateArray()
            .Single(c => c.GetProperty("database").GetString() == RolesSite.DevDatabase).GetProperty("id").GetString()!;
        var plain = await _site.RunAsync("apply", PlanFile, "--db", "prod");
        Assert.Equal(0, (await _site.RunAsync("db", "role", dev, "test")).ExitCode);

        var sealing = await _site.RunAsync("apply", PlanFile, "--dry-run", "--seal-for", "prod");

        AssertError(plain, 3, "refused", "prodDatabase");
        Assert.Contains("--seal-for", plain.Error().GetProperty("hint").GetString(), StringComparison.Ordinal);
        AssertError(sealing, 3, "refused", Sealing.ValidationReason);
    }

    private async Task AddProd()
    {
        var run = await _site.RunAsync("db", "add", "prod", "--role", "prod", "--connection", Prod);
        Assert.True(run.ExitCode == 0, run.ToString());
    }

    private static SealStore Store() => SealStore.For(OptiCliEnvironment.FromProcess());

    private static bool Used(string id) => File.Exists(Path.ChangeExtension(Store().PathOf(id), ".used"));

    /// <summary>A seal of the plan as it is now, as sealing writes one (sealing itself needs the databases).</summary>
    private async Task<Seal> SealAsync(Func<Seal, Seal>? change = null)
    {
        var environment = OptiCliEnvironment.FromProcess();
        var project = ProjectLocator.Locate(_site.ProjectFile, environment.CurrentDirectory);
        var plan = await PlanSnapshot.ReadAsync(PlanFile, CancellationToken.None);
        var key = DatabaseKey.Of("localhost,1433", ProdDatabase)!;
        var state = new ProductionState("m", [new SealedItem(Guid.Parse(Page), true, false, null, "StandardPage", "About us", [new SealedBranch("en", 7, null, 7)])]);
        var options = new SealOptions(UpdateExisting: false, Publish: false, RequestApproval: false);
        var seal = new Seal(SealStore.NewId(), DateTimeOffset.UtcNow, "apply", project.Directory, new SealTarget("localhost,1433", ProdDatabase, key.Server, key.Database),
            plan.Path, plan.Hash, plan.Files.Count, options, BuildFingerprint.Of(project, Path.Combine(project.Directory, "bin", "Debug", "net8.0", "Web.dll"), environment),
            state, SealSummary.Of(plan.Plan, options, state), $"{RolesSite.DevDatabase} on localhost,1433", "0.0.0");
        seal = change?.Invoke(seal) ?? seal;
        Store().Save(seal);
        return seal;
    }

    private static void AssertError(Run run, int exit, string code, string? reason = null)
    {
        Assert.True(run.ExitCode == exit, run.ToString());
        var error = run.Error();
        Assert.Equal(code, error.GetProperty("code").GetString());
        if (reason is not null)
        {
            Assert.Equal(reason, error.GetProperty("details").GetProperty("reason").GetString());
        }
    }

    /// <summary>The person at the terminal: answers from <see cref="Lines"/>, a password from <see cref="Hidden"/>.</summary>
    private sealed class FakeTerminal : ITerminal
    {
        private readonly System.Text.StringBuilder _output = new();

        public bool Interactive { get; set; } = true;

        public Queue<string> Lines { get; } = new();

        public Queue<string> Hidden { get; } = new();

        public string Output => _output.ToString();

        public bool IsInteractive => Interactive;

        public void WriteLine(string text = "") => _output.AppendLine(text);

        public void Write(string text) => _output.Append(text);

        public string? ReadLine() => Lines.Count > 0 ? Lines.Dequeue() : null;

        public string ReadHidden(string question)
        {
            _output.Append(question);
            return Hidden.Count > 0 ? Hidden.Dequeue() : "";
        }
    }
}
