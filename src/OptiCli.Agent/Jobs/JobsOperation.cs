using System.Globalization;
using System.Security.Principal;
using EPiServer.DataAbstraction;
using EPiServer.Scheduler;
using EPiServer.Scheduler.Internal;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Agent.Safety;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Jobs;

/// <summary>
/// <c>POST /v1/jobs/run|stop|set</c>: starts, stops and reschedules scheduled jobs through the CMS's own executor and
/// repository, as the admin UI's Scheduled Jobs page does, so the scheduler, its cache and its log see the same as for a
/// job started there.
/// </summary>
/// <remarks>
/// <para>Here and not in <c>OptiCli.Cms</c>, which the MCP module compiles in: jobs are the developer's only, and a
/// production site must never be able to run or reschedule them through opticli.</para>
/// <para>The executor runs a job whatever <c>SchedulerOptions.Enabled</c> says, so <c>jobs run</c> works while
/// <c>serve</c> keeps the scheduler off. It records the run as the scheduler does: <c>IsRunning</c>, pings and status
/// messages in <c>tblScheduledItem</c> while it runs, one <c>tblScheduledItemLog</c> row when it ends.</para>
/// </remarks>
internal static class JobsOperation
{
    /// <summary>
    /// The CMS marks a job that stopped pinging for this many ping intervals as no longer running (its process died), and
    /// restarts it if it is restartable (<c>SchedulerConst.PingsThreshold</c>).
    /// </summary>
    internal const int PingsThreshold = 4;

    public static JobRunResult Run(AgentRequest request, JobRunRequest body)
    {
        RequireLocal(request);
        var job = Require(request, body.Job);
        var executor = request.Service<IScheduledJobExecutor>();
        var ping = request.Service<IOptions<SchedulerOptions>>().Value.PingTime;
        if (DestructiveJobs.Find(job.TypeName) is { } what && !body.AllowDestructive)
        {
            throw new AgentException(AgentErrorCodes.Refused, DestructiveJobs.Refusal(job.Name, what), DestructiveJobs.Hint);
        }
        if (RunningHere(executor, job.ID) is not null)
        {
            throw AgentException.Conflict($"'{job.Name}' is running already in this site{Since(job.ID)}.", $"Wait for it (`opticli jobs log \"{job.Name}\"`), or stop it with `opticli jobs stop \"{job.Name}\"`.");
        }

        var warnings = new List<string>();
        if (job.IsRunning)
        {
            if (!Stale(job, ping))
            {
                throw AgentException.Conflict(
                    $"'{job.Name}' is running in another process against this database (last ping {job.SecondsAfterLastPing} s ago).",
                    "Wait for it to finish, or stop it where it runs (that site's admin UI, Scheduled Jobs).");
            }
            warnings.Add($"'{job.Name}' was marked as running, but whatever ran it stopped pinging {job.SecondsAfterLastPing} s ago (the process stopped).");
        }
        if (!DestructiveJobs.IsBuiltIn(job.TypeName))
        {
            warnings.Add(JobRequests.CustomJobWarning);
        }
        if (body.DryRun)
        {
            return new JobRunResult { Job = State(job, false), DryRun = true, Warnings = warnings.Count > 0 ? warnings : null };
        }

        var since = DateTime.UtcNow;
        var task = Start(executor, job);
        if (task.IsCompleted && task.Result is { Status: ScheduledJobExecutionStatus.UnableToStart } refused)
        {
            throw AgentException.Conflict($"The CMS didn't start '{job.Name}': {refused.Message}");
        }
        Started[job.ID] = since;
        return new JobRunResult { Job = State(job, true), Started = true, Since = since, Warnings = warnings.Count > 0 ? warnings : null };
    }

    public static JobStopResult Stop(AgentRequest request, JobStopRequest body)
    {
        RequireLocal(request);
        var job = Require(request, body.Job);
        var executor = request.Service<IScheduledJobExecutor>();
        var running = RunningHere(executor, job.ID);
        if (running is null)
        {
            throw job.IsRunning && !Stale(job, request.Service<IOptions<SchedulerOptions>>().Value.PingTime)
                ? AgentException.Conflict(
                    $"'{job.Name}' isn't running in the site `opticli serve` runs, but in another process against this database (last ping {job.SecondsAfterLastPing} s ago).",
                    "Stop it where it runs (that site's admin UI, Scheduled Jobs).")
                : AgentException.Conflict($"'{job.Name}' is not running.", $"`opticli jobs log \"{job.Name}\"` shows how its last run ended.");
        }
        // The instance knows best; before it is created the executor holds a placeholder, and the repository's flag counts.
        var stoppable = running is ScheduledJobBase instance ? instance.IsStoppable : job.IsStoppable;
        if (!stoppable)
        {
            throw AgentException.Refused(
                $"'{job.Name}' can't be stopped: its class doesn't support it (IsStoppable is false).",
                $"Wait for it to finish (`opticli jobs log \"{job.Name}\"`), or stop the site (`opticli serve --stop`), which aborts it.");
        }
        if (body.DryRun)
        {
            return new JobStopResult(State(job, true), false, true);
        }
        executor.Cancel(job.ID);
        return new JobStopResult(State(job, true), true);
    }

    public static JobSetResult Set(AgentRequest request, JobSetRequest body)
    {
        RequireLocal(request);
        var job = Require(request, body.Job);
        var running = RunningHere(request.Service<IScheduledJobExecutor>(), job.ID) is not null;
        var before = State(job, running);
        var changes = new List<string>();
        var warnings = new List<string>();

        if (body.Enabled is { } enabled && enabled != job.IsEnabled)
        {
            changes.Add($"enabled: {Bool(job.IsEnabled)} → {Bool(enabled)}");
            job.IsEnabled = enabled;
        }

        var nextRun = NextRun(job);
        if (body.Every is { } every)
        {
            if (!JobIntervals.TryParse(every, out var type, out var length, out var error))
            {
                throw AgentException.Usage(error!, $"--every takes {JobIntervals.Syntax}.");
            }
            var current = (Type: (int)job.IntervalType, Length: job.IntervalLength);
            if (type == 0 && body.Next is null && nextRun is not null)
            {
                // A manual job has no next run: the scheduler would still start it once at that time.
                nextRun = null;
            }
            else if (type != 0 && nextRun is null && body.Next is null)
            {
                throw AgentException.Usage(
                    $"'{job.Name}' has no next run, so with only --every the scheduler would never start it.",
                    "Give --next too: `--next now` (the scheduler's next round), or a time.");
            }
            if (JobIntervals.Describe(type, length) != JobIntervals.Describe(current.Type, current.Length))
            {
                changes.Add($"schedule: {JobIntervals.Describe(current.Type, current.Length)} → {JobIntervals.Describe(type, length)}");
                job.IntervalType = (ScheduledIntervalType)type;
                job.IntervalLength = type == 0 ? 0 : length;
            }
        }
        if (body.Next is { } next)
        {
            nextRun = DateTime.SpecifyKind(next, DateTimeKind.Utc);
        }
        if (nextRun != NextRun(job))
        {
            changes.Add($"next run: {Time(NextRun(job))} → {Time(nextRun)}");
            // The repository saves NextExecution, converted to UTC: a UTC value goes in as it is; MinValue saves NULL.
            job.NextExecution = nextRun ?? DateTime.MinValue;
            job.NextExecutionUTC = nextRun ?? DateTime.MinValue;
        }

        if (nextRun is not null && body.Next is not null)
        {
            if (!job.IsEnabled)
            {
                warnings.Add($"'{job.Name}' is disabled, so the scheduler doesn't start it at its next run; --enabled true does.");
            }
            else if (!request.Service<IOptions<SchedulerOptions>>().Value.Enabled)
            {
                warnings.Add("The scheduler is off in this site, so nothing starts it at its next run here: `opticli jobs run` runs it now, and the site with its scheduler on runs it then (`opticli serve --scheduler`).");
            }
        }

        var saved = false;
        if (changes.Count > 0 && !body.DryRun)
        {
            request.Service<IScheduledJobRepository>().Save(job);
            saved = true;
            job = Require(request, body.Job);
        }
        return new JobSetResult(before, State(job, running), changes, body.DryRun, saved)
        {
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>When this agent started each job it started (UTC), to say for how long one has been running.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> Started = new();

    /// <summary>
    /// Starts the job on a thread of its own, outside the request: with the request's context it would see the agent's
    /// request as its HttpContext (gone once the response is sent), and the principal the agent restores afterwards. It
    /// runs as <see cref="AgentProtocol.PrincipalName"/> instead, through the CMS's fallback principal
    /// (<c>Thread.CurrentPrincipal</c>, which flows to the executor's task), the identity a save from the job gets.
    /// </summary>
    /// <returns>The executor's task, which ends when the job does.</returns>
    private static Task<JobExecutionResult> Start(IScheduledJobExecutor executor, ScheduledJob job)
    {
        var principal = OptiCliPrincipal.Create();
        Task<JobExecutionResult>? task = null;
        Exception? failure = null;
        // A thread of its own, started without the request's execution context (UnsafeStart): a task waited on here could
        // run inline, with that context after all.
        var thread = new Thread(() =>
        {
            try
            {
                Thread.CurrentPrincipal = principal;
                task = executor.StartAsync(job, new JobExecutionOptions { Trigger = ScheduledJobTrigger.User }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "opticli job start",
        };
        thread.UnsafeStart();
        // StartAsync itself returns at once (it runs the job on a task of its own), after a database update.
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
        return task!;
    }

    /// <summary>The job's instance (or the executor's placeholder before it exists) when this site runs it.</summary>
    internal static IScheduledJob? RunningHere(IScheduledJobExecutor executor, Guid id) =>
        executor.ListRunningJobs().OfType<IScheduledJob>().FirstOrDefault(j => j.ID == id);

    /// <summary>Marked running, but no ping for as long as the CMS takes to count it as dead.</summary>
    internal static bool Stale(ScheduledJob job, TimeSpan pingTime) =>
        job.IsRunning && job.SecondsAfterLastPing >= PingsThreshold * pingTime.TotalSeconds;

    /// <exception cref="AgentException">Shared mode.</exception>
    private static void RequireLocal(AgentRequest request)
    {
        if (request.Service<AgentSettings>().SharedDatabase)
        {
            throw AgentException.Refused(JobRequests.SharedRefusal, JobRequests.SharedHint);
        }
    }

    private static ScheduledJob Require(AgentRequest request, Guid id) =>
        request.Service<IScheduledJobRepository>().Get(id)
        ?? throw AgentException.NotFound($"No scheduled job has the id {id}.", "`opticli jobs --all` lists them; the site registers its jobs when it starts.");

    internal static JobState State(ScheduledJob job, bool running)
    {
        var type = (int)job.IntervalType;
        return new JobState(
            job.ID,
            job.Name,
            job.TypeName,
            job.IsEnabled,
            JobIntervals.Describe(type, job.IntervalLength),
            JobIntervals.Short(type, job.IntervalLength),
            NextRun(job),
            running || job.IsRunning,
            job.IsStoppable);
    }

    private static DateTime? NextRun(ScheduledJob job) =>
        job.NextExecutionUTC == DateTime.MinValue ? null : DateTime.SpecifyKind(job.NextExecutionUTC, DateTimeKind.Utc);

    private static string Since(Guid id) =>
        Started.TryGetValue(id, out var since) ? $" (started by opticli {(int)(DateTime.UtcNow - since).TotalSeconds} s ago)" : "";

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Time(DateTime? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "none";
}
