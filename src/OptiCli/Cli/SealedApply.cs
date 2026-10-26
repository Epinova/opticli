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
/// database's name and the write login (and, when the site uses the CMS's Azure providers, production's event bus and blob
/// storage: <see cref="SealedProvidersRun"/>), starts the site against production for this run only (<see cref="SealedSite"/>),
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

    /// <summary>Production's servers don't hear about the run's writes (Findings 9 of the content-sync plan), unless the run had the event bus and it worked.</summary>
    public const string RestartWarning =
        "Production's servers keep showing what they have cached: the run's site wasn't on production's event bus (none given at the run, or the cache drops couldn't be confirmed), so nothing told them. "
        + "Changed and moved content stays as it was there (a moved page also keeps answering at its old URL), and new content is missing "
        + "from menus and listings, until the site restarts or an item goes unread for the content cache's sliding expiration (12 hours by "
        + "default; content read more often never refreshes). Content no server had cached shows at once. Restart the site (every instance; "
        + "on DXP the environment's web app) to show these changes.";

    /// <summary>A run that stopped after saving something: the undo hints of a plan run are opticli commands, which production refuses.</summary>
    public const string PartialHint =
        "details lists what was saved; its undo hints are opticli commands for a development database, which production refuses: undo those changes in production's edit UI (or seal a plan that puts them back).";

    /// <summary>A hint about <c>serve</c> (the site stopped answering), which doesn't apply to the run's own site, made to point at its log.</summary>
    internal static string? SiteHint(string? hint, string logPath) =>
        hint?.Contains("opticli serve", StringComparison.Ordinal) == true
            ? $"The site started for this run stopped (its log: {logPath}), and nothing more is written. Check what production has (`opticli get <ref> --db <name>`) before sealing again."
            : hint;

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
        Sealing.RequireNoIdeHook(output.Dll);
        var build = BuildFingerprint.Of(project, output.Dll, environment);
        var verified = SealCheck.Verify(seal, candidate.Target!, project.Directory, plan, build, DateTimeOffset.UtcNow);
        SealedPlan.Require(plan.Plan, model: null);
        var outputFolder = Path.GetDirectoryName(output.Dll)!;
        SealedProvidersRun.RequireMediaPossible(plan.Plan, outputFolder);
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
        // Microsoft Entra ID: the site signs in with the token this connection just got (the identity checked here), and
        // fetches none of its own (in a starting site that could stall until Azure SQL dropped the login).
        SealedSqlToken? sqlToken = null;
        if (credentials is null && ProductionWrite.LastEntraToken() is { } entra)
        {
            sqlToken = new SealedSqlToken(entra.AccessToken, entra.ExpiresOn);
            terminal.WriteLine($"The run's site signs in to production with this run's Microsoft Entra token (valid until {entra.ExpiresOn.ToLocalTime():HH:mm}).");
        }
        if (await WriteAccessReader.ReadAsync(session.Db, cancellationToken) is { Count: 0 })
        {
            terminal.WriteLine("warning: that login has none of db_owner, db_datawriter or INSERT/UPDATE on tblContent; the CMS may not be able to save.");
        }
        // Production's event bus (optional) and blob storage (when the plan uploads), checked before the seal is used.
        var providers = await SealedProvidersRun.AskAsync(terminal, plan.Plan, project.Directory, outputFolder, write.IsLocal, cancellationToken);
        // From here on the seal is used, whatever happens.
        store.Consume(seal.Id);

        var startup = await StartupDriftCheck.RunAsync(session.Db, outputFolder, cancellationToken);
        startup.ThrowIfBlocked(allowPendingMigrations: false);
        var folder = Path.Combine(environment.StateDirectory, "sealed");
        var driftFile = WriteDrift(folder, seal.Id, startup.Drift);
        var choice = AgentSelection.Choose(PackageVersions.FindCms(project.Project), await session.Db.SchemaAsync(cancellationToken), write.Database);
        var agentDll = AgentLocator.Locate(AppContext.BaseDirectory, choice.CmsMajor);
        var port = PortSelector.Select(null, null, PortClaims.ForUser.FreeAndClaimed(PortSelector.IsFree));
        var launch = new SealedLaunch(project, write, context.ConnectionRequest.Name, output, agentDll, choice.CmsMajor, port, StartTimeout, driftFile,
            Path.Combine(folder, $"{seal.Id}.log"), KestrelEndpoints.Configured(project, environment), providers?.Providers, sqlToken);
        var allowedTypes = new AllowedTypesCheck(session.Model, () => CSharpSourceIndex.Build(project.SourceRoot));

        PlanRun run;
        SealedEventsResult? events = null;
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
                    if (providers is not null)
                    {
                        await providers.RequireInEffectAsync(client.SealedStatusAsync, token);
                    }
                    // What the build and production differ in: the leftovers the seal accepted by name, and nothing else.
                    var acceptDrift = SealedLeftovers.Accept(await client.DriftAsync(token), seal.Leftovers);
                    terminal.WriteLine("The site answers. Checking the plan against production, then writing…");
                    var executor = new WriteExecutor(session, _ => Task.FromResult(client), site: null, project.Directory, seal.Options.UpdateExisting, acceptDrift: acceptDrift, sharedDatabase: true)
                    {
                        // Uploads send the bytes the run read and hashed, never the file on disk again.
                        Files = plan,
                        BlobsOnDisk = providers?.Providers.Blobs is null,
                    };
                    var runner = new PlanRunner(session, executor, plan.Directory, allowOutside: false, allowedTypes, seal.Options.RequestApproval)
                    {
                        Files = plan,
                        MovesFirst = seal.Options.MovesFirst,
                        // Right before the first save: the site on production's event bus, then production once more.
                        BeforeSaving = async check =>
                        {
                            if (providers is not null)
                            {
                                await providers.BeforeSavingAsync(client.SealedStatusAsync, ProbeName(seal.Id), check);
                            }
                            SealCheck.RequireUnchanged(verified, await ProductionState.ReadAsync(session, touched, named, check));
                        },
                    };
                    try
                    {
                        return await runner.RunAsync(plan.Plan, dryRun: false, seal.Options.Publish, token);
                    }
                    finally
                    {
                        // While the site still runs: what its event provider queued is sent, and the run's messages counted.
                        events = providers is null ? null : await providers.FinishEventsAsync(client.FlushEventsAsync);
                    }
                },
                cancellationToken);
        }
        catch (OptiCliException ex) when (ex.Details is PlanRun partial && partial.Operations.Any(o => o.Status == PlanStepStatus.Saved))
        {
            // What was saved is in production: its files are checked and its events reported, as after a whole run.
            events = SendFailures(events, launch.LogPath);
            var files = providers is null ? [] : await providers.CheckMediaAsync(session, partial, CancellationToken.None);
            var restart = RestartNeeded(events);
            throw OptiCliException.Create(ex.Code, ex.Message,
                $"{SiteHint(ex.Hint, launch.LogPath)} {PartialHint} {MediaHint(files)} {(restart ? Restart(events) : EventsSent(events!))}".Trim(),
                new { partial.DryRun, partial.Operations, partial.Created, partial.Partial, events, media = providers?.Providers.Blobs is null ? null : files, restartNeeded = restart });
        }
        catch (OptiCliException ex) when (ex.Hint?.Contains("opticli serve", StringComparison.Ordinal) == true)
        {
            // Hints written for `serve`, which never runs against production.
            throw OptiCliException.Create(ex.Code, ex.Message, SiteHint(ex.Hint, launch.LogPath), ex.Details);
        }
        finally
        {
            PortClaims.ForUser.ReleaseAll();
        }

        // A cache drop the site's provider couldn't send leaves that content stale in production.
        events = SendFailures(events, launch.LogPath);
        // Every uploaded file must be where production's database now points (never throws: what can't be checked is reported).
        var media = providers is null ? [] : await providers.CheckMediaAsync(session, run, CancellationToken.None);
        var restartNeeded = RestartNeeded(events);
        if (media.Where(m => !m.Found).ToList() is { Count: > 0 } missing)
        {
            throw new ConflictException(
                $"The run saved, but {missing.Count} uploaded file(s) aren't confirmed in production's blob storage: {Describe(missing)}. Production's database points at them.",
                $"{MediaHint(missing)} {(restartNeeded ? Restart(events) : EventsSent(events!))}")
            {
                Details = new { reason = SealedProvidersRun.BlobMissingReason, media, events, restartNeeded, run },
            };
        }

        var result = new
        {
            seal = seal.Id,
            target = new SealedDatabase(write.Server, write.Database, "prod"),
            plan = plan.Path,
            run,
            events,
            media = providers?.Providers.Blobs is null ? null : media,
            restartNeeded,
            restart = restartNeeded ? RestartWarning : null,
        };
        return new CommandResult(result, Text: Text(seal, run, events, media), Warnings: restartNeeded ? [Restart(events)] : [], Source: WriteExecutor.AgentSource);
    }

    /// <summary>
    /// The CMS's Azure event provider logs a cache drop it couldn't send ("Failed to send event") and goes on: one in the
    /// run's log means production's servers missed it, however many others were counted on the topic.
    /// </summary>
    internal static SealedEventsResult? SendFailures(SealedEventsResult? events, string logPath)
    {
        if (events is not { Problem: null })
        {
            return events;
        }
        try
        {
            // Before the agent's "queue sent" line: the run's own cache removals. After it: what the site raised while it
            // stopped, once the queue no longer took anything (listed, but not the plan's).
            var lines = File.ReadLines(logPath).ToList();
            var flushed = lines.FindIndex(l => l.Contains(OptiCli.Protocol.AgentProtocol.EventsFlushedLine, StringComparison.Ordinal));
            static bool Failed(string line) => line.Contains("Failed to send event", StringComparison.Ordinal);
            var before = (flushed < 0 ? lines : lines.Take(flushed)).Count(Failed);
            var after = flushed < 0 ? 0 : lines.Skip(flushed).Count(Failed);
            events = events with { NotSentWhileStopping = after };
            return before == 0 ? events : events with { Problem = $"the site's event provider logged {before} event(s) of the run it couldn't send (see {logPath})" };
        }
        catch (IOException ex)
        {
            return events with { Problem = $"the run's log couldn't be read to check for events that weren't sent: {ex.Message}" };
        }
    }

    private static string Describe(IEnumerable<SealedMediaResult> media) =>
        string.Join(", ", media.Select(m => $"{m.Blob ?? "(no file)"} (step {m.Step}, {m.Problem ?? (m.Stored is { } stored ? $"{stored} of {m.Bytes} bytes" : "not found")})"));

    /// <summary>What to do about uploaded files that aren't confirmed in the container; empty when all are.</summary>
    private static string MediaHint(IEnumerable<SealedMediaResult> media) =>
        media.Where(m => !m.Found).ToList() is { Count: > 0 } missing
            ? $"Uploaded file(s) not confirmed in production's blob storage: {Describe(missing)}; check them in production's edit UI and upload them again there where they are missing."
            : "";

    /// <summary>Whether production's servers still need a restart: no event bus, or the run's cache drops weren't confirmed on it.</summary>
    internal static bool RestartNeeded(SealedEventsResult? events) => events is not { Problem: null, Sent: > 0 };

    /// <summary>The restart warning, with why the event bus didn't do it when it was given.</summary>
    private static string Restart(SealedEventsResult? events) =>
        events?.Problem is { } problem ? $"{RestartWarning} (The event bus was given, but {problem}.)" : RestartWarning;

    private static string EventsSent(SealedEventsResult events) =>
        $"Production's servers were told over the event bus ({events.Sent} message(s) on topic '{events.Topic}'): no restart is needed."
        + (events.NotSentWhileStopping > 0 ? $" ({events.NotSentWhileStopping} cache removal(s) the run's site raised while it stopped, after its queue was sent, weren't sent: the site's own, not the plan's.)" : "");

    /// <summary>The probe subscription that counts the run's messages (Service Bus allows 50 characters).</summary>
    internal static string ProbeName(string sealId) => $"opticli-probe-{sealId}";

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
        if (seal.Leftovers is { Count: > 0 } leftovers)
        {
            terminal.WriteLine($"  accepts: {leftovers.Count} content type(s)/properties only production's database has (removed code), as they are; the plan doesn't write them, and any other difference stops the run: {Printable.Of(string.Join(", ", leftovers))}");
        }
        terminal.WriteLine($"  never:   deletes, unpublish steps, access rights{(summary.Media > 0 ? "; media files only to production's blob storage, asked next" : "")}");
        terminal.WriteLine();
    }

    private static string Text(Seal seal, PlanRun run, SealedEventsResult? events, IReadOnlyList<SealedMediaResult> media)
    {
        int Count(string status) => run.Operations.Count(o => o.Status == status);
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Applied sealed plan {seal.Id} to '{seal.Target.Database}' on '{seal.Target.Server}': {run.Operations.Count} step(s), {Count(PlanStepStatus.Saved)} saved, {Count(PlanStepStatus.Unchanged)} unchanged.");
        if (media.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Media: {media.Count} file(s) in production's blob storage, each checked.");
        }
        text.AppendLine(RestartNeeded(events) ? "Restart needed: production's servers weren't told (see the warning)." : EventsSent(events!));
        return text.ToString();
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
