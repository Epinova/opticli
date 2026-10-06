using OptiCli.Core.Errors;
using OptiCli.Core.Jobs;

namespace OptiCli.Core.Tests.Jobs;

/// <summary>
/// <c>jobs run</c> waiting for a run by polling the job tables, against a stand-in for the database whose answers change
/// as time passes. Time is the sum of the waits, so the tests run at once.
/// </summary>
public class JobWaitTests
{
    private static readonly JobRow Job = new(Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001"), "Content import", true, null, null, null, null, null, 0,
        "Example.Jobs.ImportJob", "Example", false, null, null, true, false, false);

    private static readonly JobLogRow Finished = new(42, Job.Id, Job.Name, DateTime.UtcNow, TimeSpan.FromSeconds(3), 1, 2, "web01", "Done");

    /// <summary>Answers by the time waited so far: the state and the log row as they would be then.</summary>
    private sealed class Progress(Func<TimeSpan, JobRow?> state, Func<TimeSpan, JobLogRow?> finished) : IJobProgress
    {
        public TimeSpan Now { get; set; }

        public int Polls { get; private set; }

        public Task<JobLogRow?> FinishedAsync(CancellationToken cancellationToken)
        {
            Polls++;
            return Task.FromResult(finished(Now));
        }

        public Task<JobRow?> StateAsync(CancellationToken cancellationToken) => Task.FromResult(state(Now));

        public Task Delay(TimeSpan pause, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Now += pause;
            return Task.CompletedTask;
        }
    }

    private static JobRow Running(string? status = null, int ping = 5) => Job with { Running = true, StatusMessage = status, SecondsSincePing = ping };

    [Fact]
    public async Task It_returns_the_runs_log_row_and_reports_each_new_status_message_once()
    {
        var progress = new Progress(
            now => Running(now < TimeSpan.FromSeconds(2) ? "Step 1 of 2" : "Step 2 of 2"),
            now => now >= TimeSpan.FromSeconds(5) ? Finished : null);
        var messages = new List<string>();

        var row = await JobRunner.WaitAsync(progress, Job, null, messages.Add, CancellationToken.None, progress.Delay);

        Assert.Same(Finished, row);
        Assert.Equal(["Step 1 of 2", "Step 2 of 2"], messages);
        // 500 ms first, backing off to 2 s: a handful of polls, not one every half second.
        Assert.InRange(progress.Polls, 4, 8);
    }

    [Fact]
    public async Task A_timeout_stops_the_waiting_and_leaves_the_job_running()
    {
        var progress = new Progress(_ => Running("Step 3 of 30"), _ => null);

        var ex = await Assert.ThrowsAsync<TimedOutException>(() => JobRunner.WaitAsync(progress, Job, TimeSpan.FromSeconds(10), null, CancellationToken.None, progress.Delay));

        Assert.Equal("'Content import' is still running after 10 s (Step 3 of 30); it goes on running.", ex.Message);
        Assert.Contains("opticli jobs stop \"Content import\"", ex.Hint);
        Assert.Equal(TimeSpan.FromSeconds(10), progress.Now);
    }

    [Fact]
    public async Task A_job_that_stops_pinging_ends_the_waiting()
    {
        var progress = new Progress(now => Running(ping: now < TimeSpan.FromSeconds(4) ? 5 : 600), _ => null);

        var ex = await Assert.ThrowsAsync<UnreachableException>(() => JobRunner.WaitAsync(progress, Job, null, null, CancellationToken.None, progress.Delay));

        Assert.Contains("stopped pinging 600 s ago", ex.Message);
    }

    [Fact]
    public async Task A_job_that_isnt_running_and_logs_nothing_for_a_minute_ends_the_waiting()
    {
        var progress = new Progress(_ => Job, _ => null);

        var ex = await Assert.ThrowsAsync<UnreachableException>(() => JobRunner.WaitAsync(progress, Job, null, null, CancellationToken.None, progress.Delay));

        Assert.Contains("isn't running and logged no run for 60 s", ex.Message);
        Assert.InRange(progress.Now, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(63));
    }

    [Fact]
    public async Task A_job_marked_as_not_running_that_logs_its_run_soon_after_is_fine()
    {
        // A stop marks the job as not running at once; the job logs its run when its code returns.
        var progress = new Progress(_ => Job, now => now >= TimeSpan.FromSeconds(20) ? Finished : null);

        Assert.Same(Finished, await JobRunner.WaitAsync(progress, Job, null, null, CancellationToken.None, progress.Delay));
    }

    [Fact]
    public async Task Interrupting_the_waiting_says_the_job_goes_on()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new Progress(_ => Running(), _ => null);

        cancel.Cancel();
        var ex = await Assert.ThrowsAsync<CancelledException>(() => JobRunner.WaitAsync(progress, Job, null, null, cancel.Token, progress.Delay));

        Assert.Equal("Stopped waiting (interrupted); 'Content import' goes on running in the site.", ex.Message);
    }
}
