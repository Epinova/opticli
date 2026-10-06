using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Jobs;
using OptiCli.Core.Output;
using OptiCli.Core.SourceScan;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// Lists scheduled jobs and their runs (database reads), and runs, stops and reschedules them through the site
/// (<c>run</c>, <c>stop</c>, <c>set</c>).
/// </summary>
internal static class JobsCommand
{
    private const int DefaultLogLimit = 20;

    public static Command Create(GlobalOptions options)
    {
        var all = new Option<bool>("--all") { Description = "Also list jobs the CMS hides from the admin UI (Hidden)." };
        var enabled = new Option<bool>("--enabled") { Description = "Only enabled jobs." };
        var disabled = new Option<bool>("--disabled") { Description = "Only disabled jobs." };
        var failed = new Option<bool>("--failed") { Description = "Only jobs whose last run failed, couldn't start or was aborted." };
        var list = new ListOptions(options);
        var command = new Command("jobs", """
            List the site's scheduled jobs: schedule, next and last run, how the last run ended, and whether one runs now.
            Read from the database (tblScheduledItem); nothing needs to run. overdue: enabled with a next run that has passed,
            so the scheduler starts it as soon as it runs. running is "stale" when the process that ran it stopped pinging.
            class is the job's C# class, source its file in the site's code (null for a job from a package); a job in the code
            that the database doesn't have yet is listed with registered: false (the site registers jobs when it starts), and
            one of the site's own assemblies whose class is gone with inCode: false (the CMS leaves a removed job's row).
            `jobs log` has their runs; `jobs run`, `jobs stop` and `jobs set` go through the site (needs `opticli serve`).
            Example: opticli jobs --failed
            """);
        command.Options.Add(all);
        command.Options.Add(enabled);
        command.Options.Add(disabled);
        command.Options.Add(failed);
        list.AddTo(command);
        command.Subcommands.Add(Log(options));
        command.Subcommands.Add(Run(options));
        command.Subcommands.Add(Stop(options));
        command.Subcommands.Add(Set(options));

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            if (parse.GetValue(enabled) && parse.GetValue(disabled))
            {
                throw new UsageException("--enabled and --disabled exclude each other; leave both out for every job.");
            }
            IReadOnlyList<JobRow> rows;
            await using (var db = await context.OpenDatabaseAsync(cancellationToken))
            {
                rows = await JobReader.ListAsync(db, cancellationToken);
            }
            var warnings = new List<string>();
            var project = context.TryGetProject(out _);
            var views = JobViews.List(rows, Sources(context, warnings), project is null ? new HashSet<string>() : ScheduledJobSources.Assemblies(project.SourceRoot), DateTime.UtcNow, parse.GetValue(all))
                .Where(v => !parse.GetValue(enabled) || v.Enabled)
                .Where(v => !parse.GetValue(disabled) || !v.Enabled)
                .Where(v => !parse.GetValue(failed) || v.LastStatus is JobStatuses.Failed or JobStatuses.UnableToStart or JobStatuses.Aborted)
                .ToList();
            if (views.Where(v => v.Running is "stale").Select(v => v.Name).ToList() is { Count: > 0 } stale)
            {
                warnings.Add($"Marked as running, but the process that ran {(stale.Count == 1 ? "it" : "them")} stopped pinging (it stopped, or runs on another server): {string.Join(", ", stale)}. The CMS restarts a restartable job when a site with its scheduler on starts.");
            }
            if (views.Where(v => v.InCode == false).Select(v => v.Name).ToList() is { Count: > 0 } removed)
            {
                warnings.Add($"Their class is gone from the site's code, but the CMS keeps the row (a run can't start): {string.Join(", ", removed)}. Remove such a job in the admin UI if it is gone for good.");
            }
            if (views.Where(v => v.Registered == false).Select(v => v.Name).ToList() is { Count: > 0 } unregistered)
            {
                warnings.Add($"In the code but not in the database yet: {string.Join(", ", unregistered)}. {JobReferences.UnregisteredHint}");
            }
            var page = list.Apply(parse, views);
            return new CommandResult(page.Items, page.Next, Warnings: warnings.Count > 0 ? warnings : null);
        });
        return command;
    }

    private static Command Log(GlobalOptions options)
    {
        var job = JobArgument();
        job.Arity = ArgumentArity.ZeroOrOne;
        job.Description = $"The job: {JobReferences.Syntax}. Without it: the latest runs of every job.";
        var failed = new Option<bool>("--failed") { Description = "Only runs that failed, couldn't start or were aborted." };
        var since = new Option<string?>("--since") { Description = $"Only runs that ended since: {JobTimes.SinceSyntax}.", HelpName = "date|age" };
        var list = new ListOptions(options);
        list.Limit.Description = $"Maximum runs per page (default {DefaultLogLimit}).";
        var command = new Command("log", """
            The runs of a job, latest first, from the CMS's job log (tblScheduledItemLog): when it started and finished, how
            long it took, status (succeeded, failed, cancelled = stopped by a user, unableToStart, aborted = the site shut
            down), trigger (scheduler, user, restart) and server, and the job's message in full (it can be HTML). Without a
            job: the latest runs of every job, e.g. what failed last night. The CMS keeps the latest 1000 runs per job.
            Example: opticli jobs log --failed --since 1d
            Example: opticli jobs log "Publish Delayed Content Versions" --limit 5
            """);
        command.Arguments.Add(job);
        command.Options.Add(failed);
        command.Options.Add(since);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var from = parse.GetValue(since) is { } text ? JobTimes.ParseSince(text, DateTime.UtcNow) : (DateTime?)null;
            var (offset, limit) = Paging.Window(parse.GetValue(list.Limit) ?? DefaultLogLimit, parse.GetValue(list.Cursor));
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            Guid? id = null;
            if (parse.GetValue(job) is { } reference)
            {
                var rows = await JobReader.ListAsync(db, cancellationToken);
                id = JobReferences.Resolve(reference, rows, Sources(context, null)).Id;
            }
            var runs = await JobReader.LogAsync(db, new JobLogQuery(id, parse.GetValue(failed), from), offset, limit + 1, cancellationToken);
            return CommandResult.From(Paging.FromWindow(runs.Select(JobViews.From).ToList(), offset, limit));
        });
        return command;
    }

    private static Command Run(GlobalOptions options)
    {
        var job = JobArgument();
        var noWait = new Option<bool>("--no-wait") { Description = "Return once the job has started, without waiting for it to end: { started, job, since }." };
        var timeout = new Option<int?>("--timeout") { Description = "Stop waiting after this many seconds (exit 4, timeout): the job goes on running. Default: wait as long as it runs.", HelpName = "seconds" };
        var allowDestructive = new Option<bool>("--allow-destructive")
        {
            Description = "Run a built-in job that deletes for good (emptying the recycle bin, removing unused files, trimming versions, truncating the change log, ...). Without it such a job is refused (exit 3). Never implied by anything else: ask the user first.",
        };
        var write = new WriteOptions();
        write.DryRun.Description = "Check that the job would be started (it exists, isn't running, isn't refused) without starting it.";
        var command = new Command("run", """
            Run a scheduled job now, through the site (needs `opticli serve`), as the admin UI's "Start manually" does, also
            while the scheduler is off. It runs as the user opticli. By default the command waits for it, reading its state
            from the database, shows its status messages on a terminal, and ends with the run's status, duration and message:
            exit 0 when it succeeded, 7 (job_failed) when it failed, couldn't start, or was stopped or aborted. Ctrl+C and
            --timeout stop the waiting, not the job. Already running: conflict (exit 5). Built-in jobs that delete for good
            need --allow-destructive (refused, exit 3, otherwise); the site's own jobs run without asking, with a warning:
            opticli can't tell what they change. Refused against a shared database (exit 3).
            Example: opticli jobs run "Publish Delayed Content Versions"
            Example: opticli jobs run MyImportJob --no-wait
            """);
        command.Arguments.Add(job);
        command.Options.Add(noWait);
        command.Options.Add(timeout);
        command.Options.Add(allowDestructive);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            if (parse.GetValue(timeout) is <= 0)
            {
                throw new UsageException("--timeout must be a positive number of seconds.");
            }
            if (parse.GetValue(timeout) is not null && (parse.GetValue(noWait) || parse.GetValue(write.DryRun)))
            {
                throw new UsageException("--timeout is how long to wait; --no-wait and --dry-run don't wait.");
            }
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var target = JobReferences.Resolve(parse.GetValue(job)!, await JobReader.ListAsync(db, cancellationToken), Sources(context, null));
            var shared = !db.ConnectionString.IsLocal;
            JobRunner.RequireRunnable(target, shared, parse.GetValue(allowDestructive));
            var agent = await context.ConnectAgentAsync(cancellationToken);
            var (view, warnings) = await JobRunner.RunAsync(agent, db, target, shared, new JobRunOptions(
                Wait: !parse.GetValue(noWait),
                Timeout: parse.GetValue(timeout) is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                AllowDestructive: parse.GetValue(allowDestructive),
                DryRun: parse.GetValue(write.DryRun),
                OnStatus: Console.IsErrorRedirected ? null : status => Console.Error.WriteLine($"[{target.Name}] {status}")), cancellationToken);
            return new CommandResult(view, Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    private static Command Stop(GlobalOptions options)
    {
        var job = JobArgument();
        var command = new Command("stop", $$"""
            Stop a job that the site `opticli serve` runs, through the site, as the admin UI's Stop does: the job's own code
            decides when it stops. Waits up to {{(int)JobRunner.StopWait.TotalSeconds}} s for the run to end and reports how it ended (normally
            cancelled). A job whose class can't be stopped is refused (exit 3); one that isn't running is a conflict (exit 5).
            Refused against a shared database (exit 3).
            Example: opticli jobs stop "Content import"
            """);
        var write = new WriteOptions();
        write.DryRun.Description = "Check that the site runs the job and can stop it, without stopping it.";
        command.Arguments.Add(job);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var target = JobReferences.Resolve(context.Parse.GetValue(job)!, await JobReader.ListAsync(db, cancellationToken), Sources(context, null));
            var shared = !db.ConnectionString.IsLocal;
            JobRunner.RequireLocal(shared);
            var (view, warnings) = await JobRunner.StopAsync(await context.ConnectAgentAsync(cancellationToken), db, target, shared, context.Parse.GetValue(write.DryRun), cancellationToken);
            return new CommandResult(view, Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    private static Command Set(GlobalOptions options)
    {
        var job = JobArgument();
        var enabled = new Option<string?>("--enabled") { Description = "Enable (true) or disable (false) the job: the scheduler only starts enabled jobs.", HelpName = "true|false" };
        var every = new Option<string?>("--every") { Description = $"How often the scheduler starts it: {JobIntervals.Syntax}. manual also clears the next run (unless --next).", HelpName = "interval" };
        var next = new Option<string?>("--next") { Description = $"When the scheduler starts it next: {JobTimes.NextSyntax}. now: its next round; a time that has passed makes it overdue. Default: as it is (a job without a next run needs it with --every).", HelpName = "time" };
        var write = new WriteOptions();
        var command = new Command("set", """
            Change a job's schedule through the site (needs `opticli serve`), as the admin UI's form does: enabled, interval and
            next run, saved through the CMS's job repository so the scheduler sees it. Schedules aren't versioned: the output
            has the job before and after. A next run in a site whose scheduler is off (the default for `opticli serve`) only
            takes effect where the scheduler runs. Refused against a shared database (exit 3).
            Example: opticli jobs set "Content import" --every 1h --next now --dry-run
            Example: opticli jobs set MyImportJob --enabled false
            """);
        command.Arguments.Add(job);
        command.Options.Add(enabled);
        command.Options.Add(every);
        command.Options.Add(next);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            bool? enable = parse.GetValue(enabled) is { } flag
                ? bool.TryParse(flag, out var value) ? value : throw new UsageException($"--enabled '{flag}' is not true or false.")
                : null;
            var interval = parse.GetValue(every);
            if (interval is not null && !JobIntervals.TryParse(interval, out _, out _, out var error))
            {
                throw new UsageException(error!, $"--every takes {JobIntervals.Syntax}.");
            }
            var when = parse.GetValue(next) is { } text ? JobTimes.ParseNext(text, DateTime.UtcNow) : (DateTime?)null;
            if (enable is null && interval is null && when is null)
            {
                throw new UsageException("Nothing to change: give --enabled, --every or --next.");
            }
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var target = JobReferences.Resolve(parse.GetValue(job)!, await JobReader.ListAsync(db, cancellationToken), Sources(context, null));
            var shared = !db.ConnectionString.IsLocal;
            JobRunner.RequireLocal(shared);
            var result = await JobRunner.SetAsync(await context.ConnectAgentAsync(cancellationToken), target, shared, enable, interval, when, parse.GetValue(write.DryRun), cancellationToken);
            var warnings = result.Warnings?.ToList() ?? [];
            if (result.Changes.Count == 0)
            {
                warnings.Add($"'{target.Name}' is already like that; nothing was saved.");
            }
            return new CommandResult(new JobSetView(result.Before, result.After, result.Changes, result.DryRun ? true : null, result.Saved), Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    /// <summary>What <c>jobs set</c> prints: the agent's result, with its warnings in <c>meta.warnings</c> only.</summary>
    private sealed record JobSetView(JobState Before, JobState After, IReadOnlyList<string> Changes, bool? DryRun, bool Saved);

    private static Argument<string> JobArgument() => new("job") { Description = $"The job: {JobReferences.Syntax}." };

    /// <summary>The jobs in the site's code; none (with a warning, when given a list) when the project isn't found.</summary>
    private static IReadOnlyList<ScheduledJobSource> Sources(CliContext context, List<string>? warnings)
    {
        if (context.TryGetProject(out var error) is not { } project)
        {
            warnings?.Add($"C# sources not scanned: {error?.Message}");
            return [];
        }
        return ScheduledJobSources.Find(CSharpSourceIndex.Build(project.SourceRoot));
    }
}
