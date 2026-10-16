using System.Globalization;
using System.Text;
using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Discovery;
using OptiCli.Core.Drift;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Seals;
using OptiCli.Core.Serve;
using OptiCli.Core.SourceScan;
using OptiCli.Core.Writes;

namespace OptiCli.Cli;

/// <summary>
/// <c>apply &lt;plan&gt; --db &lt;prod&gt; --seal &lt;id&gt;</c>: the user's run of a sealed plan against production, the only write
/// opticli makes there. Needs a person at a terminal, checks the seal against what it sees (target, project, plan and its
/// files, build, age, then production's model and the content the plan touches), shows what the plan does, asks for the
/// database's name and the write login, starts the site against production for this run only (<see cref="SealedSite"/>),
/// checks production once more right before the first save, applies, and stops the site.
/// </summary>
internal static class SealedApply
{
    /// <summary><c>details.reason</c> when there is no person at a terminal.</summary>
    public const string TerminalReason = "sealNeedsTerminal";

    /// <summary>The person at the terminal. Tests put a stand-in here; nothing on the command line or in the environment does.</summary>
    internal static ITerminal Terminal { get; set; } = new ConsoleTerminal();

    /// <summary>How long the site gets to answer.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Production's servers don't hear about the run's writes (Findings 9 of the content-sync plan).</summary>
    public const string RestartWarning =
        "Production's servers keep showing what they have cached: the run's site isn't on production's event bus, so nothing tells them. "
        + "Changed and moved content stays as it was there (a moved page also keeps answering at its old URL), and new content is missing "
        + "from menus and listings, until the site restarts or an item goes unread for the content cache's sliding expiration (12 hours by "
        + "default; content read more often never refreshes). Content no server had cached shows at once. Restart the site (every instance; "
        + "on DXP the environment's web app) to show these changes.";

    public static async Task<CommandResult> RunAsync(CliContext context, string planPath, string sealId, string database, CancellationToken cancellationToken)
    {
        var terminal = Terminal;
        var environment = context.Environment;
        var planFile = Path.GetFullPath(planPath, environment.CurrentDirectory);
        if (!terminal.IsInteractive)
        {
            throw new RefusedException(
                "A sealed apply to production runs only at a terminal (stdin, stdout and stderr), where the user confirms it and types the write login.",
                "Hand the user details.userCommand to run in their own terminal. An agent never runs it, and never fakes a terminal for it.")
            {
                Details = new { reason = TerminalReason, userCommand = Sealing.UserCommand(context, planFile, database, sealId) },
            };
        }
        var project = context.Project;
        var (candidate, saved) = Sealing.Production(context, database, "--db");
        var store = SealStore.For(environment);
        var seal = store.Load(sealId);
        var plan = await PlanSnapshot.ReadAsync(planFile, cancellationToken);
        var settings = UserConfig.ForProject(environment.UserConfigFile, project.Directory);
        var output = OutputLocator.Locate(project, null, settings?.Output, environment.CurrentDirectory);
        var build = BuildFingerprint.Of(project, output.Dll, environment);
        var verified = SealCheck.Verify(seal, candidate.Target!, project.Directory, plan, build, DateTimeOffset.UtcNow);
        SealedPlan.Require(plan.Plan, model: null);
        var touched = SealedPlan.Touched(plan.Plan);
        var named = SealedPlan.Referenced(plan.Plan);

        // The plan as the run read it; what production had then is checked right after the confirmation, before the login is asked.
        Show(terminal, verified, verified.Seal.Production);
        terminal.Write($"Type the database's name, '{Printable.Of(saved.Database)}', to write this plan to it (Enter cancels): ");
        var typed = terminal.ReadLine();
        if (!string.Equals(typed?.Trim(), saved.Database, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("Not confirmed (the database's name wasn't typed); nothing was written.", "The seal is still unused: run the command again to confirm.");
        }

        // Production as it is now, read with the saved login, which only reads.
        await using (var readOnly = await ContentSession.OpenAsync(await CmsDatabase.OpenAsync(saved, cancellationToken), cancellationToken))
        {
            SealedPlan.Require(plan.Plan, readOnly.Model);
            SealCheck.RequireUnchanged(verified, await ProductionState.ReadAsync(readOnly, touched, named, cancellationToken));
        }
        terminal.WriteLine("Production's content model and the content the plan touches are as sealed.");
        WriteCredentials? credentials = null;
        if (ProductionWrite.LoginOf(saved) == WriteLogin.Password)
        {
            terminal.WriteLine("The login to write with (used for this run only, never saved; the saved login only reads):");
            terminal.Write("  User: ");
            var user = terminal.ReadLine() ?? "";
            credentials = new WriteCredentials(user, terminal.ReadHidden("  Password (not shown): "));
        }
        else
        {
            terminal.WriteLine("The saved connection signs in with your own identity: it writes as you.");
        }
        var write = ProductionWrite.Authorize(verified, saved, typed, credentials);

        // The write login works (a typo fails here, with the seal still unused).
        CmsDatabase writeDb;
        try
        {
            writeDb = await CmsDatabase.OpenAsync(write.ForReads(), cancellationToken);
        }
        catch (LoginFailedException ex)
        {
            throw new LoginFailedException(ex.Message, "The write login typed was refused. Nothing was written and the seal is still unused: run the command again with the right login.") { Details = ex.Details };
        }
        await using var session = await ContentSession.OpenAsync(writeDb, cancellationToken);
        if (await WriteAccessReader.ReadAsync(session.Db, cancellationToken) is { Count: 0 })
        {
            terminal.WriteLine("warning: that login has none of db_owner, db_datawriter or INSERT/UPDATE on tblContent; the CMS may not be able to save.");
        }
        // From here on the seal is used, whatever happens.
        store.Consume(seal.Id);

        var startup = await StartupDriftCheck.RunAsync(session.Db, Path.GetDirectoryName(output.Dll)!, cancellationToken);
        startup.ThrowIfBlocked(allowPendingMigrations: false);
        var folder = Path.Combine(environment.StateDirectory, "sealed");
        var driftFile = WriteDrift(folder, seal.Id, startup.Drift);
        var choice = AgentSelection.Choose(PackageVersions.FindCms(project.Project), await session.Db.SchemaAsync(cancellationToken), write.Database);
        var agentDll = AgentLocator.Locate(AppContext.BaseDirectory, choice.CmsMajor);
        var port = PortSelector.Select(null, null, PortClaims.ForUser.FreeAndClaimed(PortSelector.IsFree));
        var launch = new SealedLaunch(project, write, context.ConnectionRequest.Name, output, agentDll, choice.CmsMajor, port, StartTimeout, driftFile,
            Path.Combine(folder, $"{seal.Id}.log"), KestrelEndpoints.Configured(project, environment));
        var allowedTypes = new AllowedTypesCheck(session.Model, () => CSharpSourceIndex.Build(project.SourceRoot));

        PlanRun run;
        try
        {
            run = await SealedSite.RunAsync(launch,
                pid =>
                {
                    PortClaims.ForUser.ReleaseAll();
                    terminal.WriteLine($"Started the site against production for this run (pid {pid}, http://127.0.0.1:{port}, log {launch.LogPath}); it is stopped when the run ends.");
                },
                async (client, token) =>
                {
                    terminal.WriteLine("The site answers. Checking the plan against production, then writing…");
                    var executor = new WriteExecutor(session, _ => Task.FromResult(client), site: null, project.Directory, seal.Options.UpdateExisting, sharedDatabase: true);
                    var runner = new PlanRunner(session, executor, plan.Directory, allowOutside: false, allowedTypes, seal.Options.RequestApproval)
                    {
                        Files = plan,
                        MovesFirst = seal.Options.MovesFirst,
                        // Production once more, after the site started and the plan passed its checks: right before the first save.
                        BeforeSaving = async check => SealCheck.RequireUnchanged(verified, await ProductionState.ReadAsync(session, touched, named, check)),
                    };
                    return await runner.RunAsync(plan.Plan, dryRun: false, seal.Options.Publish, token);
                },
                cancellationToken);
        }
        catch (OptiCliException ex) when (ex.Details is PlanRun partial && partial.Operations.Any(o => o.Status == PlanStepStatus.Saved))
        {
            throw OptiCliException.Create(ex.Code, ex.Message, $"{ex.Hint} {RestartWarning}".Trim(), ex.Details);
        }
        finally
        {
            PortClaims.ForUser.ReleaseAll();
        }

        var result = new
        {
            seal = seal.Id,
            target = new SealedDatabase(write.Server, write.Database, "prod"),
            plan = plan.Path,
            run,
            restart = RestartWarning,
        };
        return new CommandResult(result, Text: Text(seal, run), Warnings: [RestartWarning], Source: WriteExecutor.AgentSource);
    }

    /// <summary>What the user confirms: the target, the plan (counted from the plan the run read, not from the seal), the build and what isn't done.</summary>
    private static void Show(ITerminal terminal, VerifiedSeal verified, ProductionState state)
    {
        var seal = verified.Seal;
        var summary = SealSummary.Of(verified.Plan.Plan, seal.Options, state);
        var age = DateTimeOffset.UtcNow - seal.Created;
        terminal.WriteLine();
        terminal.WriteLine($"Sealed apply {seal.Id} to PRODUCTION: '{Printable.Of(seal.Target.Database)}' on '{Printable.Of(seal.Target.Server)}' (role prod)");
        terminal.WriteLine($"  plan:    {Printable.Of(verified.Plan.Path)} ({verified.Plan.Files.Count} file(s)), unchanged since the seal");
        terminal.WriteLine($"  sealed:  {seal.Created:u} ({(int)age.TotalHours} h {age.Minutes} min ago), checked against {Printable.Of(seal.ValidatedOn)}");
        terminal.WriteLine($"  build:   {Printable.Of(verified.Build.Output)} (built {verified.Build.Built:u}{(verified.Build.Commit is { } commit ? $", git {commit}{(verified.Build.Uncommitted == true ? " with uncommitted changes" : "")}" : "")}), unchanged since the seal: its code runs against production");
        terminal.WriteLine($"  options: {(seal.Options.Flags.Count > 0 ? string.Join(' ', seal.Options.Flags) : "(none)")}");
        terminal.WriteLine($"  steps:   {Printable.Of(Sealing.Describe(summary))}");
        terminal.WriteLine("  checks:  production's content model and the versions of the content the plan touches must be as sealed: next, and again right before the first save");
        terminal.WriteLine("  never:   deletes, unpublishing, access rights, media files");
        terminal.WriteLine();
    }

    private static string Text(Seal seal, PlanRun run)
    {
        int Count(string status) => run.Operations.Count(o => o.Status == status);
        return new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Applied sealed plan {seal.Id} to '{seal.Target.Database}' on '{seal.Target.Server}': {run.Operations.Count} step(s), {Count(PlanStepStatus.Saved)} saved, {Count(PlanStepStatus.Unchanged)} unchanged.")
            .ToString();
    }

    /// <summary>What was compared before the start, for the agent's drift report: user-only, beside the run's log.</summary>
    private static string WriteDrift(string folder, string id, OptiCli.Protocol.StartupDrift drift)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var path = Path.Combine(folder, $"{id}.drift.json");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var writer = new StreamWriter(new FileStream(path, options), new UTF8Encoding(false)))
        {
            writer.Write(System.Text.Json.JsonSerializer.Serialize(drift, OptiCli.Protocol.AgentJson.Options));
        }
        return path;
    }
}
