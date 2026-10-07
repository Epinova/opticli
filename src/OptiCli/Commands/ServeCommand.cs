using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Configuration;
using OptiCli.Core.Drift;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

/// <summary>
/// Starts, inspects and stops the local site with the opticli agent injected; write commands go through it.
/// </summary>
internal static class ServeCommand
{
    private const int DefaultTimeoutSeconds = 180;
    private const int DefaultLogLines = 200;

    /// <summary>How long a start waits for another one (which may be running <c>--build</c>) to release the project's start lock.</summary>
    private static readonly TimeSpan StartLockWait = TimeSpan.FromMinutes(10);

    public static Command Create(GlobalOptions options)
    {
        var build = new Option<bool>("--build") { Description = "Run `dotnet build` on the site project first." };
        var port = new Option<int?>("--port") { Description = $"Port on 127.0.0.1. Default: the user config's port, else {PortSelector.DefaultPort}, else the first free one up to {PortSelector.RangeEnd}.", HelpName = "n" };
        var foreground = new Option<bool>("--foreground") { Description = "Run attached, streaming the site's output (to stderr when stdout is redirected), until Ctrl+C." };
        var output = new Option<string?>("--output") { Description = "The site DLL to run. Default: bin/{Debug,Release}/<tfm>/<AssemblyName>.dll (newest), or the user config's output.", HelpName = "dll" };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for the site to answer.", DefaultValueFactory = _ => DefaultTimeoutSeconds, HelpName = "seconds" };
        var status = new Option<bool>("--status") { Description = "Show whether the site runs, its port, pid, database and agent version." };
        var logs = new Option<bool>("--logs") { Description = $"Show the latest run's log (the site's console output); data.previous lists the logs of up to {StateStore.LogsKept - 1} runs before it." };
        var tail = new Option<int?>("--tail") { Description = $"With --logs: only the last N lines (default {DefaultLogLines}).", HelpName = "n" };
        var stop = new Option<bool>("--stop") { Description = "Stop the site (asks it to shut down through the agent, else SIGTERM on Unix; kills it after 20 s) and forget its token." };
        var allowPendingMigrations = new Option<bool>("--allow-pending-migrations")
        {
            Description = "Against a shared database: start even though this build has EF Core migrations the database lacks. Without it serve refuses (exit 3), since a site that migrates at startup would apply them for everyone.",
        };
        var scheduler = new Option<bool>("--scheduler")
        {
            Description = "Leave the site's scheduler as the site sets it, so jobs run on their schedule (overdue ones at once). Default: off for every run (\"scheduler\": true in the user config turns it on; --scheduler false off again); `opticli jobs run` runs a job either way. Not against a shared database.",
        };
        var https = new Option<bool>("--https")
        {
            Description = "Also listen on https://localhost:<next free port> with the ASP.NET Core development certificate, to browse a site that redirects to HTTPS. Default: \"https\" in the user config; --https false turns it off.",
        };

        var command = new Command("serve", """
            Start, inspect or stop the local site with the opticli agent injected; write commands need it running.
            Runs the site's build output (its code is not changed) in Development on http://127.0.0.1:<port>, pinned to the
            database opticli reads; the agent refuses to start against anything else (exit 3). The agent build (CMS 12 or 13)
            follows the site's CMS version; a CMS 13 build against a CMS 12 database is refused (it would upgrade it). Starts
            in the background and returns once the agent answers (30-60 s is normal); state (pid, port, a fresh token) is kept
            in a user-only file.
            Warns when source files are newer than the build output (--build rebuilds first). A site that redirects to HTTPS
            can't be browsed on that address: --https adds https://localhost:<port> (browseUrl), while opticli keeps using HTTP.
            Against a shared (remote) development database it turns off what would change that database at startup, refuses
            to start a build with EF Core migrations the database lacks, and reports what differs between this build and the
            database (drift; `opticli drift`): writes then stop until the user confirms. Stop it when done.
            The site's scheduler is off for the run (a restored database's overdue jobs would otherwise all start: imports,
            emails, emptying the recycle bin); `opticli jobs run` still runs a job, and --scheduler leaves it on.
            Example: opticli serve    then: opticli serve --status | opticli serve --logs --tail 40 | opticli serve --stop
            """);
        foreach (var option in new Option[] { build, port, foreground, output, timeout, https, scheduler, allowPendingMigrations, status, logs, tail, stop })
        {
            command.Options.Add(option);
        }

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var modes = new[] { parse.GetValue(status), parse.GetValue(logs), parse.GetValue(stop) }.Count(m => m);
            var starting = parse.GetValue(build) || parse.GetValue(port) is not null || parse.GetValue(foreground) || parse.GetValue(output) is not null || parse.GetResult(https) is { Implicit: false } || parse.GetResult(scheduler) is { Implicit: false } || parse.GetValue(allowPendingMigrations);
            if (modes > 1 || (modes == 1 && starting))
            {
                throw new UsageException("--status, --logs and --stop are separate actions; don't combine them with each other or with start options.");
            }
            if (parse.GetValue(tail) is not null && !parse.GetValue(logs))
            {
                throw new UsageException("--tail only applies to --logs.");
            }

            if (parse.GetValue(status))
            {
                var current = await StatusAsync(context, cancellationToken);
                List<string> statusWarnings = [];
                if (DriftWarning(context.StateStore, started: false) is { } drifted)
                {
                    statusWarnings.Add(drifted);
                }
                if (current is { State: AgentState.Running, Scheduler: null, Database.Local: true })
                {
                    statusWarnings.Add(OldAgentSchedulerWarning(current, null));
                }
                return new CommandResult(current, Warnings: statusWarnings.Count > 0 ? statusWarnings : null, Source: WriteExecutor.AgentSource);
            }
            if (parse.GetValue(logs))
            {
                return Logs(context, parse.GetValue(tail) ?? DefaultLogLines);
            }
            if (parse.GetValue(stop))
            {
                return await StopAsync(context);
            }

            var seconds = parse.GetValue(timeout);
            if (seconds <= 0)
            {
                throw new UsageException("--timeout must be a positive number of seconds.");
            }
            return await StartAsync(context, parse.GetValue(build), parse.GetValue(port), parse.GetValue(foreground), parse.GetValue(output), parse.GetResult(https) is { Implicit: false } ? parse.GetValue(https) : null,
                parse.GetResult(scheduler) is { Implicit: false } ? parse.GetValue(scheduler) : null, parse.GetValue(allowPendingMigrations), TimeSpan.FromSeconds(seconds), cancellationToken);
        });
        return command;
    }

    /// <param name="scheduler">--scheduler as given; null to take the user config's.</param>
    private static async Task<CommandResult> StartAsync(CliContext context, bool build, int? port, bool foreground, string? output, bool? https, bool? scheduler, bool allowPendingMigrations, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var project = context.Project;
        // The database check comes before anything is started or even looked for.
        var connection = RequireServable(context);
        if (scheduler == true && !connection.IsLocal)
        {
            throw new UsageException(
                "--scheduler doesn't apply against a shared database: its scheduler stays off, or the jobs would run beside the deployed site's.",
                "Leave out --scheduler. Run a job in that environment's own admin UI.");
        }
        var store = context.StateStore;

        // Held until the new state file is written, so a second `serve` started meanwhile sees this one's site.
        using var startLock = await store.LockAsync(StartLockWait, cancellationToken);
        var existing = await AgentProbe.ProbeAsync(store, connection, cancellationToken);
        switch (existing.State)
        {
            case AgentState.Running when existing.Mode != ServeMode.External:
                return new CommandResult(existing, Warnings: ["The site was already running; nothing was started.", .. DriftWarning(store, started: false) is { } drifted ? [drifted] : Array.Empty<string>()],
                    Source: WriteExecutor.AgentSource);
            case AgentState.Stopped:
                break;
            case AgentState.Stale:
            case AgentState.Unresponsive when existing.Mode == ServeMode.External:
                store.Delete();
                break;
            case AgentState.Running:
                throw new UsageException(
                    $"A site started with the variables from `opticli env` is running on port {existing.Port}.",
                    "Use it as it is, or stop it (and `opticli serve --stop` to forget its token) before `opticli serve`.");
            case AgentState.Unresponsive when existing.StartedAt > DateTimeOffset.UtcNow - timeout:
                // Another `serve` started it moments ago and is waiting for it: wait too, rather than start a second site.
                startLock.Dispose();
                return await WaitForOtherStartAsync(store, connection, existing, timeout, cancellationToken);
            default:
                throw new UsageException($"A site is already registered for this project: {existing.Message}", "Check `opticli serve --status` and `--logs`; `opticli serve --stop` stops it.");
        }

        var settings = UserConfig.ForProject(context.Environment.UserConfigFile, project.Directory);
        var schedulerOn = connection.IsLocal && (scheduler ?? settings?.Scheduler ?? false);
        if (build)
        {
            await SiteBuild.RunAsync(project, Console.Error, cancellationToken);
        }
        // After a build, which restores: an unrestored project's CMS version is only known then.
        var agent = await SelectAgentAsync(context, connection, cancellationToken);
        var siteOutput = OutputLocator.Locate(project, output, settings?.Output, context.Environment.CurrentDirectory);
        var warnings = new List<string>();
        string? driftFile = null;
        if (!connection.IsLocal)
        {
            warnings.Add(SharedDatabaseWarning(connection));
            // Before anything starts: what the CMS or the site itself would change in (or refuse about) the shared database.
            StartupCheck startup;
            await using (var db = await context.OpenDatabaseAsync(cancellationToken))
            {
                startup = await StartupDriftCheck.RunAsync(db, Path.GetDirectoryName(siteOutput.Dll)!, cancellationToken);
            }
            startup.ThrowIfBlocked(allowPendingMigrations);
            warnings.AddRange(startup.Warnings);
            driftFile = store.WriteStartupDrift(startup.Drift);
        }
        if (!connection.IsLocal && scheduler is null && settings?.Scheduler == true)
        {
            warnings.Add("\"scheduler\": true in the user config doesn't apply against a shared database: the scheduler stays off.");
        }
        if (schedulerOn)
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            if (await OverdueJobsAsync(db, cancellationToken) is { Count: > 0 } overdue)
            {
                warnings.Add(OverdueWarning(overdue, "start as soon as the site's scheduler runs (--scheduler)"));
            }
        }
        if (OutputLocator.NewerSource(project, siteOutput.Dll) is { } newer)
        {
            warnings.Add($"{Path.GetRelativePath(project.Directory, newer)} is newer than the build output {Path.GetRelativePath(project.Directory, siteOutput.Dll)}; the site may be out of date (use --build).");
        }
        var siteEndpoints = KestrelEndpoints.Configured(project, context.Environment);
        if (siteEndpoints.Count > 0)
        {
            warnings.Add(KestrelEndpoints.Warning(siteEndpoints));
        }
        // Claimed, so another serve starting at the same moment doesn't pick the same port before this site listens on it.
        var free = PortClaims.ForUser.FreeAndClaimed(PortSelector.IsFree);
        var httpPort = PortSelector.Select(port, settings?.Port, free);
        var request = new LaunchRequest(
            project,
            connection,
            context.ConnectionRequest.Name,
            siteOutput,
            agent.Dll,
            httpPort,
            timeout,
            (https ?? settings?.Https == true) ? PortSelector.Select(null, null, p => p != httpPort && free(p)) : null,
            siteEndpoints,
            driftFile,
            schedulerOn,
            agent.Choice.CmsMajor);

        if (!foreground)
        {
            AgentStatus started;
            try
            {
                started = await SiteLauncher.StartBackgroundAsync(store, request, startLock, cancellationToken);
            }
            finally
            {
                // The site listens on its ports now (or didn't start): the claims have done their job.
                PortClaims.ForUser.ReleaseAll();
            }
            if (request.HttpsPort is null && started.Url is { } url && await RedirectsToHttpsAsync(url, cancellationToken))
            {
                warnings.Add($"The site redirects {url} to HTTPS, so it can't be browsed there (opticli itself is unaffected). `opticli serve --stop`, then `opticli serve --https` adds an HTTPS address.");
            }
            if (DriftWarning(store, started: true) is { } drift)
            {
                warnings.Add(drift);
            }
            if (schedulerOn && started.Scheduler == "off")
            {
                warnings.Add("The site turns its scheduler off itself (SchedulerOptions in its code or configuration): --scheduler leaves the site's own setting.");
            }
            return new CommandResult(started, Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
        }

        foreach (var warning in warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
        // Redirected stdout carries the one JSON envelope at the end, so the site's output goes to stderr then.
        await SiteLauncher.RunForegroundAsync(store, request, startLock, Console.IsOutputRedirected ? Console.Error : Console.Out,
            ready =>
            {
                PortClaims.ForUser.ReleaseAll();
                Console.Error.WriteLine($"[opticli] agent ready at {ready.Url}{(ready.BrowseUrl is { } browse ? $", browse the site at {browse}" : "")} (pid {ready.Pid}, database '{ready.Database?.Name}'); Ctrl+C stops the site.");
                if (DriftWarning(store, started: true) is { } drift)
                {
                    Console.Error.WriteLine($"warning: {drift}");
                }
            },
            cancellationToken);
        PortClaims.ForUser.ReleaseAll();
        return new CommandResult(new { stopped = true });
    }

    /// <summary>Waits for a site another <c>serve</c> is starting, and reports it like one that was already running.</summary>
    private static async Task<CommandResult> WaitForOtherStartAsync(StateStore store, Core.Safety.VerifiedConnectionString connection, AgentStatus starting, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var status = starting;
        while (status.State == AgentState.Unresponsive && waited.Elapsed < timeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            status = await AgentProbe.ProbeAsync(store, connection, cancellationToken);
        }
        return status.State switch
        {
            AgentState.Running => new CommandResult(status,
                Warnings: ["Another `opticli serve` was starting the site; this one waited for it and started nothing.", .. DriftWarning(store, started: false) is { } drifted ? [drifted] : Array.Empty<string>()],
                Source: WriteExecutor.AgentSource),
            AgentState.Stopped or AgentState.Stale => throw new UnreachableException(
                "Another `opticli serve` was starting the site, and it didn't start.",
                "Its error is in that command's output and in `opticli serve --logs`; fix it, then run `opticli serve` again."),
            _ => throw new UsageException($"Another `opticli serve` is starting the site, which still doesn't answer: {status.Message}", "Check `opticli serve --status` and `--logs`; `opticli serve --stop` stops it."),
        };
    }

    private static async Task<CommandResult> StopAsync(CliContext context)
    {
        var store = context.StateStore;
        StopResult? stopped;
        try
        {
            stopped = await SiteLauncher.StopAsync(store);
        }
        catch (CorruptStateException ex)
        {
            store.Delete();
            return new CommandResult(
                new { stopped = false, message = "Removed the corrupt state file; nothing is known to run for this project." },
                Warnings: [$"{ex.Message} If a site opticli started earlier still runs, stop it yourself."],
                Source: WriteExecutor.AgentSource);
        }
        return new CommandResult(stopped ?? (object)new { stopped = false, message = "Nothing was running for this project." }, Source: WriteExecutor.AgentSource);
    }

    /// <summary>
    /// The database the site may run against: a local one, or the remote development database the user chose. A local
    /// build writing to a shared database also syncs its content types into it, so another environment is never served.
    /// </summary>
    /// <exception cref="RefusedException">A remote database that isn't the development database.</exception>
    public static Core.Safety.VerifiedConnectionString RequireServable(CliContext context)
    {
        var connection = context.UseConnection();
        var resolution = context.ResolveConnection();
        if (!resolution.IsDevelopment)
        {
            throw new RefusedException(
                $"Not running the site against '{connection.Database}' on remote server '{connection.Server}': it is not this project's development database.",
                "The site only runs against a local database or the development database the user chose (`opticli db list`). Reads with --db work; writes to other environments are not supported.");
        }
        return connection;
    }

    /// <summary>
    /// The agent build for the site's CMS major (<see cref="AgentSelection"/>): from the project, else the database. Against
    /// a shared database both builds turn off what would change it for everyone and report drift.
    /// </summary>
    /// <exception cref="RefusedException">The project and the database disagree.</exception>
    /// <exception cref="UsageException">The major is unknown or unsupported.</exception>
    public static async Task<(AgentChoice Choice, string Dll)> SelectAgentAsync(CliContext context, Core.Safety.VerifiedConnectionString connection, CancellationToken cancellationToken)
    {
        Core.Data.CmsSchema? schema = null;
        string? databaseError = null;
        try
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            schema = await db.SchemaAsync(cancellationToken);
        }
        catch (OptiCliException ex)
        {
            // The project may still tell; the site itself reports a database it can't reach.
            databaseError = ex.Message;
        }
        var choice = AgentSelection.Choose(Core.Discovery.PackageVersions.FindCms(context.Project.Project), schema, connection.Database, databaseError);
        return (choice, AgentLocator.Locate(AppContext.BaseDirectory, choice.CmsMajor));
    }

    public static string SharedDatabaseWarning(Core.Safety.VerifiedConnectionString connection) =>
        $"The site runs against the remote development database '{connection.Database}' on '{connection.Server}', which others may use too. "
        + "For this run opticli turned off its scheduler, automatic database schema updates, content type sync and store remapping, so content types "
        + "or properties that exist only in your local code are not added to the database (writes to them fail). The site's own startup code still runs.";

    /// <summary>
    /// The site runs an agent from before opticli 0.12, which doesn't turn the scheduler off (or say whether it is on): the
    /// site's own setting holds, and overdue jobs may run there.
    /// </summary>
    /// <param name="overdue">Enabled jobs whose next run has passed; null when not counted.</param>
    internal static string OldAgentSchedulerWarning(AgentStatus agent, int? overdue) =>
        $"The running site's agent ({agent.Agent?.Version ?? "unknown version"}) is older than opticli 0.12, which turns the site's scheduler off: its scheduler may be on"
        + (overdue is { } count ? $", with {count} overdue job{(count == 1 ? "" : "s")} it would start" : "")
        + ". Restart it with this opticli: `opticli serve --stop`, then `opticli serve`.";

    /// <summary>Enabled jobs whose next run has passed: the scheduler starts each as soon as it runs.</summary>
    internal static async Task<IReadOnlyList<Core.Jobs.JobRow>> OverdueJobsAsync(Core.Data.CmsDatabase db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return (await Core.Jobs.JobReader.ListAsync(db, cancellationToken)).Where(j => j.Enabled && j.NextRun is { } next && next < now).ToList();
    }

    internal static string OverdueWarning(IReadOnlyList<Core.Jobs.JobRow> overdue, string when) =>
        $"{overdue.Count} overdue job{(overdue.Count == 1 ? "" : "s")} will {when}: {string.Join(", ", overdue.Take(10).Select(j => j.Name))}{(overdue.Count > 10 ? ", ..." : "")}. `opticli jobs` lists them.";

    /// <summary>What the agent reported as drift once the site answered, kept in the state file; null when nothing differs.</summary>
    /// <param name="started">The site was just started: the longer summary; otherwise the short warning other commands give.</param>
    private static string? DriftWarning(StateStore store, bool started)
    {
        try
        {
            return store.Read()?.Drift is { Fingerprint: not null } drift ? started ? DriftText.Started(drift) : DriftText.Warning(drift) : null;
        }
        catch (CorruptStateException)
        {
            return null;
        }
    }

    /// <summary>Whether the site answers its start page with a redirect to HTTPS; false when it can't tell quickly.</summary>
    private static async Task<bool> RedirectsToHttpsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.GetAsync(url + "/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location
                && location.IsAbsoluteUri && location.Scheme == Uri.UriSchemeHttps;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<AgentStatus> StatusAsync(CliContext context, CancellationToken cancellationToken)
    {
        var resolution = context.ResolveConnection();
        var expected = resolution.Chosen is null ? null : resolution.Require();
        var status = await AgentProbe.ProbeAsync(context.StateStore, expected, cancellationToken);
        if (status.CorruptState)
        {
            // Nothing in it can be used, and it would block `serve`: treat it as stale and remove it.
            context.StateStore.Delete();
            return status with { Message = $"{status.Message} It was removed.", Hint = $"If a site opticli started earlier still runs, stop it yourself. {AgentProbe.StartHint}" };
        }
        return status;
    }

    private static CommandResult Logs(CliContext context, int lines)
    {
        if (lines <= 0)
        {
            throw new UsageException("--tail must be a positive number of lines.");
        }
        var store = context.StateStore;
        if (!File.Exists(store.LogPath))
        {
            throw new NotFoundException("There is no site log for this project yet.", "Start the site with `opticli serve`.");
        }
        var tail = LogTail.Read(store.LogPath, lines);
        var previous = Enumerable.Range(1, StateStore.LogsKept - 1).Select(store.PreviousLogPath).Where(File.Exists).ToList();
        return new CommandResult(new { log = store.LogPath, lines = tail, previous = previous.Count > 0 ? previous : null }, Text: string.Concat(tail.Select(l => l + Environment.NewLine)));
    }
}
