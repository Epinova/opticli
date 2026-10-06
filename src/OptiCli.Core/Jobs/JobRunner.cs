using System.Diagnostics;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;
using UnreachableException = OptiCli.Core.Errors.UnreachableException;

namespace OptiCli.Core.Jobs;

/// <summary>What <c>jobs run</c> prints: the start, and, once it has waited, how the run ended.</summary>
/// <param name="Since">When the site started it (UTC).</param>
/// <param name="Status">One of <see cref="JobStatuses"/>; null when it didn't wait.</param>
/// <param name="Message">The job's message (it can be HTML); null when it didn't wait.</param>
/// <param name="Warnings">In <c>error.details</c> of a run that failed, the warnings a successful run has in <c>meta.warnings</c>.</param>
public sealed record JobRunView(
    string Job,
    Guid Id,
    bool Started,
    DateTime? Since,
    string? Status = null,
    string? Duration = null,
    long? DurationMs = null,
    DateTime? Finished = null,
    string? Message = null,
    bool? DryRun = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>What <c>jobs stop</c> prints.</summary>
/// <param name="Stopped">The job's run ended within the wait; false when it is still finishing.</param>
/// <param name="Status">How the run ended (normally <c>cancelled</c>); null while it is still finishing.</param>
public sealed record JobStopView(string Job, Guid Id, bool Stopped, string? Status = null, string? Duration = null, DateTime? Finished = null, string? Message = null, bool? DryRun = null);

/// <param name="Wait">Wait for the run to end (else return once it has started).</param>
/// <param name="Timeout">How long to wait; null for as long as it runs.</param>
/// <param name="OnStatus">Called with each new status message the job reports while it runs.</param>
public sealed record JobRunOptions(bool Wait = true, TimeSpan? Timeout = null, bool AllowDestructive = false, bool DryRun = false, Action<string>? OnStatus = null);

/// <summary>Where <see cref="JobRunner.WaitAsync"/> reads a job's state: the database, or a stand-in in tests.</summary>
public interface IJobProgress
{
    /// <summary>The job's first log row after the one that was latest before the start; null while it runs.</summary>
    Task<JobLogRow?> FinishedAsync(CancellationToken cancellationToken);

    Task<JobRow?> StateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts, stops and reschedules a job through the site agent, and waits for a run by reading the job tables: the CMS
/// writes <c>IsRunning</c>, pings and status messages to <c>tblScheduledItem</c> while the job runs, and one row to
/// <c>tblScheduledItemLog</c> as it ends. Waiting that way needs no HTTP request open as long as the job runs, and sees
/// a run that outlives the command (Ctrl+C, <c>--timeout</c>, <c>--no-wait</c>) the same way.
/// </summary>
public static class JobRunner
{
    /// <summary>How long <c>jobs stop</c> waits for the run to end.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FirstPoll = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan LastPoll = TimeSpan.FromSeconds(2);

    /// <summary>How long a job may be marked as not running, with no log row, before waiting gives up on it.</summary>
    private static readonly TimeSpan NotRunningGrace = TimeSpan.FromSeconds(60);

    /// <exception cref="RefusedException">Shared database; a destructive built-in job without <see cref="JobRunOptions.AllowDestructive"/>.</exception>
    /// <exception cref="ConflictException">It runs already (in a process that still pings).</exception>
    /// <exception cref="JobFailedException">It ran and didn't succeed; <see cref="OptiCliException.Details"/> is the <see cref="JobRunView"/>.</exception>
    /// <exception cref="TimedOutException"><see cref="JobRunOptions.Timeout"/> ran out; it goes on running.</exception>
    /// <exception cref="CancelledException">Interrupted while waiting; it goes on running.</exception>
    public static async Task<(JobRunView View, IReadOnlyList<string> Warnings)> RunAsync(
        AgentClient agent, CmsDatabase db, JobRow job, bool sharedDatabase, JobRunOptions options, CancellationToken cancellationToken)
    {
        RequireRunnable(job, sharedDatabase, options.AllowDestructive);
        var baseline = await JobReader.LatestLogIdAsync(db, job.Id, cancellationToken);
        var started = await SendAsync<JobRunResult>(agent, AgentRoutes.JobRun,
            new JobRunRequest { Job = job.Id, AllowDestructive = options.AllowDestructive, DryRun = options.DryRun }, cancellationToken);
        var warnings = started.Warnings ?? [];
        var view = new JobRunView(job.Name, job.Id, started.Started, started.Since, DryRun: started.DryRun ? true : null);
        if (!started.Started || !options.Wait)
        {
            return (view, warnings);
        }

        var run = await WaitAsync(new DatabaseProgress(db, job.Id, baseline), job, options.Timeout, options.OnStatus, cancellationToken);
        var log = JobViews.From(run);
        view = view with { Status = log.Status, Duration = log.Duration, DurationMs = log.DurationMs, Finished = log.Finished, Message = log.Message };
        if (log.Status != JobStatuses.Succeeded)
        {
            throw new JobFailedException(
                $"'{job.Name}' {Ended(log.Status)}{(JobViews.OneLine(log.Message) is { } message ? $": {message}" : ".")}",
                $"details.message has the job's message; `opticli jobs log \"{job.Name}\"` shows its earlier runs, and `opticli serve --logs` the site's log.")
            {
                Details = warnings.Count > 0 ? view with { Warnings = warnings } : view,
            };
        }
        return (view, warnings);
    }

    /// <summary>Asks the site to stop the job, then waits up to <see cref="StopWait"/> for its run to end.</summary>
    /// <exception cref="RefusedException">Shared database; a job that can't be stopped.</exception>
    /// <exception cref="ConflictException">It isn't running (in the site <c>serve</c> runs).</exception>
    /// <param name="dryRun">Only check that it runs in the site and can be stopped.</param>
    public static async Task<(JobStopView View, IReadOnlyList<string> Warnings)> StopAsync(AgentClient agent, CmsDatabase db, JobRow job, bool sharedDatabase, bool dryRun, CancellationToken cancellationToken)
    {
        RequireLocal(sharedDatabase);
        if (!job.Running)
        {
            throw new ConflictException($"'{job.Name}' is not running.", $"`opticli jobs log \"{job.Name}\"` shows how its last run ended.");
        }
        var baseline = await JobReader.LatestLogIdAsync(db, job.Id, cancellationToken);
        await SendAsync<JobStopResult>(agent, AgentRoutes.JobStop, new JobStopRequest { Job = job.Id, DryRun = dryRun }, cancellationToken);
        if (dryRun)
        {
            return (new JobStopView(job.Name, job.Id, false, DryRun: true), []);
        }
        try
        {
            var run = JobViews.From(await WaitAsync(new DatabaseProgress(db, job.Id, baseline), job, StopWait, null, cancellationToken));
            return (new JobStopView(job.Name, job.Id, true, run.Status, run.Duration, run.Finished, run.Message), []);
        }
        catch (TimedOutException)
        {
            return (new JobStopView(job.Name, job.Id, false),
                [$"'{job.Name}' was asked to stop, and hasn't ended after {StopWait.TotalSeconds:0} s: it stops once its own code checks for it. `opticli jobs log \"{job.Name}\"` shows when it ends."]);
        }
    }

    /// <exception cref="RefusedException">Shared database; a change that arms a destructive job, without <paramref name="allowDestructive"/>.</exception>
    public static Task<JobSetResult> SetAsync(AgentClient agent, JobRow job, bool sharedDatabase, bool? enabled, string? every, DateTime? next, bool allowDestructive, bool dryRun,
        CancellationToken cancellationToken)
    {
        RequireSettable(job, sharedDatabase, enabled, every, next, allowDestructive);
        return SendAsync<JobSetResult>(agent, AgentRoutes.JobSet,
            new JobSetRequest { Job = job.Id, Enabled = enabled, Every = every, Next = next, AllowDestructive = allowDestructive, DryRun = dryRun }, cancellationToken);
    }

    /// <summary>
    /// What the CLI checks before it asks the site (which checks again): the schedule <c>jobs set</c> would leave, worked
    /// out the way the site does, mustn't arm a destructive job (<see cref="DestructiveJobs.Arms"/>) without the flag.
    /// </summary>
    /// <exception cref="RefusedException">Shared database; a change that arms a destructive job.</exception>
    public static void RequireSettable(JobRow job, bool sharedDatabase, bool? enabled, string? every, DateTime? next, bool allowDestructive)
    {
        RequireLocal(sharedDatabase);
        if (allowDestructive || DestructiveJobs.SetRefusal(job.Name, job.TypeName) is not { } refusal)
        {
            return;
        }
        var before = new JobSchedule(job.Enabled, job.NextRun, JobIntervals.TypeOf(job.DatePart), job.Interval);
        var after = before with { Enabled = enabled ?? before.Enabled };
        if (every is not null && JobIntervals.TryParse(every, out var type, out var length, out _))
        {
            after = after with { IntervalType = type, IntervalLength = type == 0 ? 0 : length, NextRun = type == 0 && next is null ? null : after.NextRun };
        }
        if (next is { } at)
        {
            after = after with { NextRun = at };
        }
        if (DestructiveJobs.Arms(before, after))
        {
            throw new RefusedException(refusal, DestructiveJobs.Hint);
        }
    }

    /// <summary>
    /// Polls <paramref name="progress"/> until the run's log row appears: every 500 ms at first, backing off to every 2 s.
    /// </summary>
    /// <param name="delay">Stands in for <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in tests; with it, time is the delays' sum.</param>
    /// <exception cref="TimedOutException"><paramref name="timeout"/> ran out.</exception>
    /// <exception cref="UnreachableException">The job stopped pinging, or isn't marked running and wrote no log row for a minute.</exception>
    /// <exception cref="CancelledException">Interrupted.</exception>
    public static async Task<JobLogRow> WaitAsync(IJobProgress progress, JobRow job, TimeSpan? timeout, Action<string>? onStatus, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var clock = Stopwatch.StartNew();
        var waited = TimeSpan.Zero;
        TimeSpan Elapsed() => delay is null ? clock.Elapsed : waited;
        var interval = FirstPoll;
        string? lastStatus = null;
        TimeSpan? notRunningSince = null;
        try
        {
            while (true)
            {
                if (await progress.FinishedAsync(cancellationToken) is { } finished)
                {
                    return finished;
                }
                var state = await progress.StateAsync(cancellationToken);
                if (state?.StatusMessage is { Length: > 0 } status && status != lastStatus)
                {
                    lastStatus = status;
                    onStatus?.Invoke(status);
                }
                if (state is { Running: true } && JobViews.IsStale(state))
                {
                    throw new UnreachableException(
                        $"'{job.Name}' stopped pinging {state.SecondsSincePing} s ago without finishing: the site running it stopped.",
                        "Check the site with `opticli serve --status` and `opticli serve --logs`; the CMS restarts a restartable job when its site starts again.");
                }
                if (state is null or { Running: false })
                {
                    notRunningSince ??= Elapsed();
                    if (Elapsed() - notRunningSince > NotRunningGrace)
                    {
                        throw new UnreachableException(
                            $"'{job.Name}' isn't running and logged no run for {NotRunningGrace.TotalSeconds:0} s.",
                            $"The site may have stopped while it ran: `opticli serve --logs` shows; `opticli jobs log \"{job.Name}\"` shows whether it ended since.");
                    }
                }
                else
                {
                    notRunningSince = null;
                }
                if (timeout is { } limit && Elapsed() >= limit)
                {
                    throw new TimedOutException(
                        $"'{job.Name}' is still running after {limit.TotalSeconds:0} s{(lastStatus is null ? "" : $" ({lastStatus})")}; it goes on running.",
                        StopHint(job))
                    {
                        Details = new { job = job.Name, id = job.Id, statusMessage = lastStatus },
                    };
                }
                var pause = timeout is { } end && end - Elapsed() < interval ? end - Elapsed() : interval;
                await (delay ?? Task.Delay)(pause, cancellationToken);
                waited += pause;
                interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 1.5, LastPoll.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new CancelledException($"Stopped waiting (interrupted); '{job.Name}' goes on running in the site.", StopHint(job));
        }
    }

    private static string StopHint(JobRow job) =>
        $"`opticli jobs stop \"{job.Name}\"` stops it{(job.Stoppable ? "" : " (if it can be stopped)")}; `opticli jobs log \"{job.Name}\"` shows how it ended.";

    private static string Ended(string status) => status switch
    {
        JobStatuses.Failed => "failed",
        JobStatuses.UnableToStart => "couldn't start",
        JobStatuses.Cancelled => "was stopped",
        JobStatuses.Aborted => "was aborted (the site shut down)",
        _ => $"ended with status {status}",
    };

    /// <summary>What the CLI checks before it asks the site (which checks again): no site needs to run for a refusal.</summary>
    /// <exception cref="RefusedException">Shared database; a destructive built-in job without <paramref name="allowDestructive"/>.</exception>
    /// <exception cref="ConflictException">It runs already (in a process that still pings).</exception>
    public static void RequireRunnable(JobRow job, bool sharedDatabase, bool allowDestructive)
    {
        RequireLocal(sharedDatabase);
        if (DestructiveJobs.Refusal(job.Name, job.TypeName) is { } refusal && !allowDestructive)
        {
            throw new RefusedException(refusal, DestructiveJobs.Hint);
        }
        if (JobViews.IsRunning(job))
        {
            throw new ConflictException($"'{job.Name}' is running already (last ping {job.SecondsSincePing} s ago).", StopHint(job));
        }
    }

    /// <exception cref="RefusedException">Shared database.</exception>
    public static void RequireLocal(bool sharedDatabase)
    {
        if (sharedDatabase)
        {
            throw new RefusedException(JobRequests.SharedRefusal, JobRequests.SharedHint);
        }
    }

    /// <exception cref="NotFoundException">The site's agent is older than the jobs routes (or the job is gone).</exception>
    private static async Task<T> SendAsync<T>(AgentClient agent, string route, object body, CancellationToken cancellationToken)
    {
        try
        {
            return await agent.SendAsync<T>(HttpMethod.Post, route, body, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            throw new NotFoundException("The site's agent is older than this opticli and can't run, stop or change jobs.", AgentErrors.OutOfDateHint);
        }
    }

    private sealed class DatabaseProgress(CmsDatabase db, Guid job, long baseline) : IJobProgress
    {
        public Task<JobLogRow?> FinishedAsync(CancellationToken cancellationToken) => JobReader.LogAfterAsync(db, job, baseline, cancellationToken);

        public Task<JobRow?> StateAsync(CancellationToken cancellationToken) => JobReader.GetAsync(db, job, cancellationToken);
    }
}
