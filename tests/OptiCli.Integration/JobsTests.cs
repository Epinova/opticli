using OptiCli.Core.Errors;
using OptiCli.Core.Jobs;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// <c>jobs</c>, <c>jobs log</c>, <c>jobs run|stop|set</c> on the edge-case site's "opticli test job" (JobsFixture.cs),
/// through the running site. Each test puts the job's schedule back and removes the marker files it made.
/// </summary>
public sealed class JobsTests
{
    /// <summary>The fixture's <c>[ScheduledPlugIn(GUID = ...)]</c>, which the CMS takes as the job's id.</summary>
    private static readonly Guid FixtureJob = Guid.Parse("5E0B1D2C-7A3F-4B6E-9D10-3C4B5A6F7E81");

    [SiteFact]
    public async Task Jobs_lists_the_built_in_jobs_without_a_source_and_the_fixture_job_with_its_file()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is null)
        {
            return;
        }

        var rows = await JobReader.ListAsync(site.Session.Db, cancellationToken);
        var views = JobViews.List(rows, ScheduledJobSources.Find(CSharpSourceIndex.Build(site.ProjectDirectory)), ScheduledJobSources.Assemblies(site.ProjectDirectory), DateTime.UtcNow, all: false);

        var fixture = views.Single(v => v.Id == FixtureJob);
        Assert.Equal(("opticli test job", "manual", true, "OptiCliEdgeCases.OptiCliTestJob"), (fixture.Name, fixture.Schedule, fixture.Stoppable, fixture.Class));
        Assert.StartsWith("EdgeCases/JobsFixture.cs:", fixture.Source);
        var trash = views.Single(v => v.Class == "EPiServer.Util.EmptyWastebasketJob");
        Assert.Null(trash.Source);
        Assert.Equal("every 1 week", trash.Schedule);
        Assert.DoesNotContain(views, v => v.Registered == false || v.InCode == false);
    }

    [SiteFact]
    public async Task Run_waits_for_the_job_and_ends_with_its_message_as_the_opticli_user()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } job)
        {
            return;
        }
        var messages = new List<string>();

        var (view, warnings) = await JobRunner.RunAsync(site.Agent, site.Session.Db, job, sharedDatabase: false, new JobRunOptions(OnStatus: messages.Add), cancellationToken);

        Assert.Equal((true, JobStatuses.Succeeded, "Done: 3 steps, as opticli."), (view.Started, view.Status, view.Message));
        Assert.InRange(view.DurationMs!.Value, 2500, 30_000);
        Assert.Contains("Step 3 of 3", messages);
        Assert.Equal([JobRequests.CustomJobWarning], warnings);
        var logged = (await JobReader.LogAsync(site.Session.Db, new JobLogQuery(FixtureJob), 0, 1, cancellationToken)).Single();
        Assert.Equal((1, 2, "Done: 3 steps, as opticli."), (logged.Status, logged.Trigger, logged.Text));
    }

    [SiteFact]
    public async Task A_run_that_fails_is_job_failed_with_the_jobs_message()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } job)
        {
            return;
        }
        var marker = AppData(site, OptiCliTestJob.FailMarker);
        File.WriteAllText(marker, "");
        try
        {
            var ex = await Assert.ThrowsAsync<JobFailedException>(() => JobRunner.RunAsync(site.Agent, site.Session.Db, job, false, new JobRunOptions(), cancellationToken));

            Assert.Equal(ErrorCode.JobFailed, ex.Code);
            Assert.Equal(7, ExitCodes.For(ex.Code));
            Assert.StartsWith("'opticli test job' failed: The opticli test job failed on purpose", ex.Message);
            Assert.Equal(JobStatuses.Failed, Assert.IsType<JobRunView>(ex.Details).Status);
            var failed = await JobReader.LogAsync(site.Session.Db, new JobLogQuery(FixtureJob, FailedOnly: true), 0, 1, cancellationToken);
            Assert.Contains("failed on purpose", failed.Single().Text);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [SiteFact]
    public async Task A_run_that_wasnt_waited_for_can_be_stopped_and_ends_as_cancelled()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } job)
        {
            return;
        }
        var steps = AppData(site, OptiCliTestJob.StepsFile);
        File.WriteAllText(steps, "30");
        try
        {
            var (started, _) = await JobRunner.RunAsync(site.Agent, site.Session.Db, job, false, new JobRunOptions(Wait: false), cancellationToken);
            Assert.True(started.Started);
            Assert.Null(started.Status);

            var running = await RunningAsync(site, cancellationToken);
            var conflict = await Assert.ThrowsAsync<ConflictException>(() => JobRunner.RunAsync(site.Agent, site.Session.Db, running, false, new JobRunOptions(), cancellationToken));
            var (stopped, _) = await JobRunner.StopAsync(site.Agent, site.Session.Db, running, false, dryRun: false, cancellationToken);

            Assert.Contains("is running already", conflict.Message);
            Assert.Equal((true, JobStatuses.Cancelled), (stopped.Stopped, stopped.Status));
            Assert.StartsWith("Stopped at step", stopped.Message);
            var after = await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken);
            Assert.False(after!.Running);
            await Assert.ThrowsAsync<ConflictException>(() => JobRunner.StopAsync(site.Agent, site.Session.Db, after, false, false, cancellationToken));
        }
        finally
        {
            File.Delete(steps);
        }
    }

    [SiteFact]
    public async Task Set_changes_the_schedule_that_jobs_then_shows_and_a_dry_run_saves_nothing()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } job)
        {
            return;
        }
        try
        {
            var dry = await JobRunner.SetAsync(site.Agent, job, false, null, "1h", DateTime.UtcNow, dryRun: true, cancellationToken);
            Assert.Equal((false, "every 1 hour"), (dry.Saved, dry.After.Schedule));
            Assert.Equal("manual", JobViews.Schedule((await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken))!));

            var set = await JobRunner.SetAsync(site.Agent, job, false, null, "1h", DateTime.UtcNow, dryRun: false, cancellationToken);
            var row = (await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken))!;
            Assert.True(set.Saved);
            Assert.Equal(("every 1 hour", true), (JobViews.Schedule(row), JobViews.From(row, null, DateTime.UtcNow.AddSeconds(1)).Overdue));
            // The scheduler is off in the site `serve` runs: an overdue job is only started by `jobs run`.
            Assert.Contains(set.Warnings!, w => w.Contains("scheduler is off", StringComparison.Ordinal));

            var disabled = await JobRunner.SetAsync(site.Agent, job, false, false, null, null, dryRun: false, cancellationToken);
            Assert.Equal(["enabled: true → false"], disabled.Changes);
            Assert.False((await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken))!.Enabled);
        }
        finally
        {
            await JobRunner.SetAsync(site.Agent, job, false, true, "manual", null, dryRun: false, cancellationToken);
            var back = (await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken))!;
            Assert.Equal(("manual", true, (DateTime?)null), (JobViews.Schedule(back), back.Enabled, back.NextRun));
        }
    }

    [SiteFact]
    public async Task A_destructive_built_in_job_is_refused_without_the_flag_by_the_cli_and_the_site()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var trash = (await JobReader.ListAsync(site.Session.Db, cancellationToken)).Single(j => j.TypeName == "EPiServer.Util.EmptyWastebasketJob");

        var cli = await Assert.ThrowsAsync<RefusedException>(() => JobRunner.RunAsync(site.Agent, site.Session.Db, trash, false, new JobRunOptions(), cancellationToken));
        var agent = await Assert.ThrowsAsync<RefusedException>(() => site.Agent.SendAsync<JobRunResult>(HttpMethod.Post, AgentRoutes.JobRun, new JobRunRequest { Job = trash.Id }, cancellationToken));
        var dry = await site.Agent.SendAsync<JobRunResult>(HttpMethod.Post, AgentRoutes.JobRun, new JobRunRequest { Job = trash.Id, AllowDestructive = true, DryRun = true }, cancellationToken);

        Assert.Contains("permanently deletes the content in the recycle bin", cli.Message);
        Assert.Contains("--allow-destructive", agent.Message);
        Assert.Equal((false, true), (dry.Started, dry.DryRun));
        Assert.Equal(trash.LastRun, (await JobReader.GetAsync(site.Session.Db, trash.Id, cancellationToken))!.LastRun);
    }

    [SiteFact]
    public async Task The_site_serve_started_has_its_scheduler_off_and_starts_no_overdue_job()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var ping = await site.Agent.PingAsync(cancellationToken);
        if (ping.Scheduler != false)
        {
            // Started with `serve --scheduler` (or by an agent older than the field): nothing to check here.
            return;
        }
        var before = (await JobReader.ListAsync(site.Session.Db, cancellationToken)).Where(j => j.Enabled && j.NextRun < DateTime.UtcNow && !j.Running).ToList();
        if (before.Count == 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

        var after = await JobReader.ListAsync(site.Session.Db, cancellationToken);
        foreach (var job in before)
        {
            var now = after.Single(j => j.Id == job.Id);
            Assert.False(now.Running, $"{job.Name} runs although the scheduler is off.");
            Assert.Equal((job.NextRun, job.LastRun), (now.NextRun, now.LastRun));
        }
    }

    /// <summary>The fixture job, not running; null (the test then passes without checking) on a site without JobsFixture.cs.</summary>
    private static async Task<JobRow?> FixtureAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        var job = await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken);
        for (var i = 0; job is { Running: true } && i < 60; i++)
        {
            // A run an earlier test left going.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            job = await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken);
        }
        return job;
    }

    /// <summary>The fixture job once the CMS has marked it as running.</summary>
    private static async Task<JobRow> RunningAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await JobReader.GetAsync(site.Session.Db, FixtureJob, cancellationToken) is { Running: true } running)
            {
                return running;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        throw new InvalidOperationException("The fixture job didn't start.");
    }

    private static string AppData(SiteUnderTest site, string file) => Path.Combine(site.ProjectDirectory, "App_Data", file);

    /// <summary>The fixture's marker files (JobsFixture.cs isn't compiled here).</summary>
    private static class OptiCliTestJob
    {
        public const string FailMarker = "opticli-job-fail";

        public const string StepsFile = "opticli-job-steps";
    }
}
