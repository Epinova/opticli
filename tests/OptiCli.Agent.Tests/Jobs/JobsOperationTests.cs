using System.Collections;
using EPiServer.DataAbstraction;
using EPiServer.Scheduler;
using EPiServer.Scheduler.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Http;
using OptiCli.Agent.Jobs;
using OptiCli.Cms;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Jobs;

/// <summary>Running, stopping and rescheduling jobs through stand-ins for the CMS's job repository and executor.</summary>
public class JobsOperationTests
{
    private static readonly Guid ImportId = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001");
    private static readonly Guid TrashId = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000002");

    private static ScheduledJob Job(Guid id, string name, string typeName, bool stoppable = true) => new()
    {
        ID = id,
        Name = name,
        TypeName = typeName,
        AssemblyName = "Example",
        MethodName = "Execute",
        IsStoppable = stoppable,
    };

    private sealed class Repository(params ScheduledJob[] jobs) : IScheduledJobRepository
    {
        public List<ScheduledJob> Saved { get; } = [];

        public void Delete(Guid id) => throw new NotSupportedException();

        public ScheduledJob? Get(Guid id) => jobs.FirstOrDefault(j => j.ID == id);

        public ScheduledJob Get(string method, string typeName, string assemblyName) => throw new NotSupportedException();

        public IEnumerable<ScheduledJob> List() => jobs;

        public void Save(ScheduledJob job) => Saved.Add(job);
    }

    private sealed class Executor : IScheduledJobExecutor
    {
        public static readonly AsyncLocal<string?> RequestValue = new();

        public List<IScheduledJob> Running { get; } = [];

        public List<Guid> Cancelled { get; } = [];

        /// <summary>The principal and the request's async-local value the job would have seen.</summary>
        public (string? Principal, string? Request)? StartedWith { get; private set; }

        public JobExecutionResult? Result { get; set; }

        public Task<JobExecutionResult> StartAsync(ScheduledJob job, JobExecutionOptions options, CancellationToken cancellationToken)
        {
            Assert.Equal(ScheduledJobTrigger.User, options.Trigger);
            StartedWith = (Thread.CurrentPrincipal?.Identity?.Name, RequestValue.Value);
            return Result is { } result ? Task.FromResult(result) : new TaskCompletionSource<JobExecutionResult>().Task;
        }

        public void Cancel(Guid id) => Cancelled.Add(id);

        public IEnumerable ListRunningJobs() => Running;
    }

    private sealed class Placeholder(Guid id) : IScheduledJob
    {
        public Guid ID => id;

        public string Execute() => throw new NotSupportedException();
    }

    private sealed class Instance : ScheduledJobBase
    {
        public Instance(Guid id, bool stoppable)
        {
            ScheduledJobId = id;
            IsStoppable = stoppable;
        }

        public override string Execute() => "";
    }

    private static AgentRequest Request(Repository repository, Executor executor, bool shared = false, bool schedulerOn = false) =>
        new(new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IScheduledJobRepository>(repository)
                .AddSingleton<IScheduledJobExecutor>(executor)
                .AddSingleton(Options.Create(new SchedulerOptions { Enabled = schedulerOn }))
                .AddSingleton(shared ? Settings(pinned: Remote, approvedRemote: RemoteApproval) : Settings(pinned: Local))
                .BuildServiceProvider(),
        }, null);

    [Fact]
    public void A_job_starts_outside_the_request_as_the_opticli_user()
    {
        var repository = new Repository(Job(ImportId, "Content import", "Example.Jobs.ImportJob"));
        var executor = new Executor();
        Executor.RequestValue.Value = "the agent's request";

        var result = JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = ImportId });

        Assert.True(result.Started);
        Assert.NotNull(result.Since);
        Assert.Equal(("opticli", (string?)null), executor.StartedWith);
        Assert.Equal([JobRequests.CustomJobWarning], result.Warnings);
    }

    [Fact]
    public void A_destructive_built_in_job_is_refused_without_the_flag_and_starts_with_it()
    {
        var repository = new Repository(Job(TrashId, "Automatic Emptying of Trash", "EPiServer.Util.EmptyWastebasketJob"));
        var executor = new Executor();

        var refused = Assert.Throws<AgentException>(() => JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = TrashId }));
        var started = JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = TrashId, AllowDestructive = true });

        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Contains("permanently deletes the content in the recycle bin", refused.Message);
        Assert.True(started.Started);
        Assert.Null(started.Warnings);
    }

    [Fact]
    public void Nothing_is_run_stopped_or_changed_against_a_shared_database()
    {
        var repository = new Repository(Job(ImportId, "Content import", "Example.Jobs.ImportJob"));
        var executor = new Executor();

        var run = Assert.Throws<AgentException>(() => JobsOperation.Run(Request(repository, executor, shared: true), new JobRunRequest { Job = ImportId, DryRun = true }));
        var stop = Assert.Throws<AgentException>(() => JobsOperation.Stop(Request(repository, executor, shared: true), new JobStopRequest { Job = ImportId }));
        var set = Assert.Throws<AgentException>(() => JobsOperation.Set(Request(repository, executor, shared: true), new JobSetRequest { Job = ImportId, Enabled = false }));

        Assert.All(new[] { run, stop, set }, ex => Assert.Equal((AgentErrorCodes.Refused, JobRequests.SharedRefusal), (ex.Code, ex.Message)));
        Assert.Null(executor.StartedWith);
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public void A_job_that_runs_here_or_elsewhere_is_a_conflict_but_a_stale_one_starts_with_a_warning()
    {
        var job = Job(ImportId, "Content import", "Example.Jobs.ImportJob");
        var repository = new Repository(job);
        var executor = new Executor();
        executor.Running.Add(new Placeholder(ImportId));

        var here = Assert.Throws<AgentException>(() => JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = ImportId }));
        executor.Running.Clear();
        job.IsRunning = true;
        job.SecondsAfterLastPing = 20;
        var elsewhere = Assert.Throws<AgentException>(() => JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = ImportId }));
        job.SecondsAfterLastPing = 600;
        var stale = JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = ImportId });

        Assert.Equal(AgentErrorCodes.Conflict, here.Code);
        Assert.Contains("running already in this site", here.Message);
        Assert.Equal(AgentErrorCodes.Conflict, elsewhere.Code);
        Assert.Contains("in another process", elsewhere.Message);
        Assert.True(stale.Started);
        Assert.Contains(stale.Warnings!, w => w.Contains("stopped pinging 600 s ago", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dry_run_starts_nothing_and_a_job_the_cms_wont_start_is_a_conflict()
    {
        var repository = new Repository(Job(ImportId, "Content import", "Example.Jobs.ImportJob"));
        var executor = new Executor();

        var dry = JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = ImportId, DryRun = true });
        executor.Result = new JobExecutionResult(ScheduledJobExecutionStatus.UnableToStart, "An instance of the scheduled job is already running.", null);
        var refused = Assert.Throws<AgentException>(() => JobsOperation.Run(Request(repository, executor), new JobRunRequest { Job = ImportId }));

        Assert.Equal((false, true), (dry.Started, dry.DryRun));
        Assert.Equal(AgentErrorCodes.Conflict, refused.Code);
        Assert.Contains("already running", refused.Message);
    }

    [Fact]
    public void Stop_cancels_a_stoppable_job_running_here_and_refuses_the_rest()
    {
        var repository = new Repository(Job(ImportId, "Content import", "Example.Jobs.ImportJob"), Job(TrashId, "Unstoppable", "Example.Jobs.Unstoppable", stoppable: false));
        var executor = new Executor();

        var notRunning = Assert.Throws<AgentException>(() => JobsOperation.Stop(Request(repository, executor), new JobStopRequest { Job = ImportId }));
        executor.Running.Add(new Instance(ImportId, stoppable: true));
        executor.Running.Add(new Instance(TrashId, stoppable: false));
        var dry = JobsOperation.Stop(Request(repository, executor), new JobStopRequest { Job = ImportId, DryRun = true });
        var stopping = JobsOperation.Stop(Request(repository, executor), new JobStopRequest { Job = ImportId });
        var unstoppable = Assert.Throws<AgentException>(() => JobsOperation.Stop(Request(repository, executor), new JobStopRequest { Job = TrashId }));

        Assert.Equal(AgentErrorCodes.Conflict, notRunning.Code);
        Assert.Equal((false, true), (dry.Stopping, dry.DryRun));
        Assert.True(stopping.Stopping);
        Assert.Equal([ImportId], executor.Cancelled);
        Assert.Equal(AgentErrorCodes.Refused, unstoppable.Code);
    }

    [Fact]
    public void Set_changes_the_interval_and_next_run_and_saves_through_the_repository()
    {
        var job = Job(ImportId, "Content import", "Example.Jobs.ImportJob");
        var repository = new Repository(job);
        var next = new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

        var result = JobsOperation.Set(Request(repository, new Executor()), new JobSetRequest { Job = ImportId, Every = "6h", Next = next });

        Assert.True(result.Saved);
        Assert.Equal(["schedule: manual → every 6 hours", "next run: none → 2026-10-07T03:00:00Z"], result.Changes);
        Assert.Equal((ScheduledIntervalType.Hours, 6, next), (job.IntervalType, job.IntervalLength, job.NextExecution));
        Assert.Equal(DateTimeKind.Utc, job.NextExecution.Kind);
        Assert.Equal(("manual", "every 6 hours", "6h"), (result.Before.Schedule, result.After.Schedule, result.After.Every));
        Assert.Contains(result.Warnings!, w => w.Contains("scheduler is off", StringComparison.Ordinal));
        Assert.Same(job, Assert.Single(repository.Saved));
    }

    [Fact]
    public void Set_refuses_an_interval_without_a_next_run_and_manual_clears_it()
    {
        var job = Job(ImportId, "Content import", "Example.Jobs.ImportJob");
        var repository = new Repository(job);

        var noNext = Assert.Throws<AgentException>(() => JobsOperation.Set(Request(repository, new Executor()), new JobSetRequest { Job = ImportId, Every = "1h" }));
        var tooOften = Assert.Throws<AgentException>(() => JobsOperation.Set(Request(repository, new Executor()), new JobSetRequest { Job = ImportId, Every = "30s", Next = DateTime.UtcNow }));
        job.IntervalType = ScheduledIntervalType.Days;
        job.IntervalLength = 1;
        job.NextExecution = job.NextExecutionUTC = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        var manual = JobsOperation.Set(Request(repository, new Executor(), schedulerOn: true), new JobSetRequest { Job = ImportId, Every = "manual" });

        Assert.Equal(AgentErrorCodes.Usage, noNext.Code);
        Assert.Contains("--next", noNext.Hint);
        Assert.Equal(AgentErrorCodes.Usage, tooOften.Code);
        Assert.Equal(["schedule: every 1 day → manual", "next run: 2026-10-07T00:00:00Z → none"], manual.Changes);
        Assert.Equal((ScheduledIntervalType.None, 0, DateTime.MinValue), (job.IntervalType, job.IntervalLength, job.NextExecution));
        Assert.Null(manual.Warnings);
    }

    [Fact]
    public void Set_without_a_change_and_a_dry_run_save_nothing()
    {
        var job = Job(ImportId, "Content import", "Example.Jobs.ImportJob");
        var repository = new Repository(job);

        var same = JobsOperation.Set(Request(repository, new Executor()), new JobSetRequest { Job = ImportId, Enabled = true });
        var dry = JobsOperation.Set(Request(repository, new Executor()), new JobSetRequest { Job = ImportId, Enabled = false, DryRun = true });

        Assert.Equal((false, 0), (same.Saved, same.Changes.Count));
        Assert.Equal((false, true, false), (dry.Saved, dry.DryRun, dry.After.Enabled));
        Assert.Equal(["enabled: true → false"], dry.Changes);
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public void An_unknown_job_is_not_found()
    {
        var ex = Assert.Throws<AgentException>(() => JobsOperation.Run(Request(new Repository(), new Executor()), new JobRunRequest { Job = ImportId }));

        Assert.Equal(AgentErrorCodes.NotFound, ex.Code);
    }
}
