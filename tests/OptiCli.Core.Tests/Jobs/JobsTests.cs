using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Jobs;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Jobs;

/// <summary>Schedules, statuses, job references and times, as <c>jobs</c>, <c>jobs log</c> and <c>jobs set</c> read them.</summary>
public class JobsTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid ImportId = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001");

    private static JobRow Job(string name, string? typeName = null, Guid? id = null, string? datePart = null, int interval = 0, DateTime? next = null, bool enabled = true,
        bool running = false, int? ping = null, int? lastStatus = null, string? lastText = null, bool hidden = false) =>
        new(id ?? Guid.NewGuid(), name, enabled, null, lastStatus, lastText, next, datePart, interval, typeName ?? $"Example.Jobs.{name.Replace(" ", "")}", "Example",
            running, null, ping, true, false, hidden);

    private static readonly IReadOnlyList<JobRow> Jobs =
    [
        Job("Content import", "Example.Jobs.ImportJob", ImportId),
        Job("Content export", "Example.Jobs.ExportJob"),
        Job("Remove Abandoned BLOBs", "EPiServer.Util.BlobCleanupJob"),
        Job("Search index", "Example.Search.IndexJob"),
        Job("Other index", "Example.Other.IndexJob"),
    ];

    [Theory]
    [InlineData(null, 0, "manual", null)]
    [InlineData("mi", 30, "every 30 minutes", "30m")]
    [InlineData("hh", 1, "every 1 hour", "1h")]
    [InlineData("dd", 1, "every 1 day", "1d")]
    [InlineData("wk", 2, "every 2 weeks", "2w")]
    [InlineData("mm", 1, "every 1 month", "1mo")]
    [InlineData("yy", 1, "every 1 year", "1y")]
    [InlineData("ss", 10, "every 10 seconds", "10s")]
    [InlineData("hh", 0, "manual", null)]
    [InlineData("xx", 5, "manual", null)]
    public void Date_parts_and_intervals_read_as_a_schedule(string? datePart, int interval, string schedule, string? every)
    {
        var type = JobIntervals.TypeOf(datePart);

        Assert.Equal(schedule, JobIntervals.Describe(type, interval));
        Assert.Equal(every, JobIntervals.Short(type, interval));
    }

    [Theory]
    [InlineData("30m", 6, 30)]
    [InlineData("1h", 5, 1)]
    [InlineData("6H", 5, 6)]
    [InlineData("1d", 4, 1)]
    [InlineData("1w", 3, 1)]
    [InlineData("1mo", 2, 1)]
    [InlineData("1y", 1, 1)]
    [InlineData("2hours", 5, 2)]
    [InlineData("1MO", 2, 1)]
    [InlineData("12H", 5, 12)]
    [InlineData("Manual", 0, 0)]
    [InlineData("manual", 0, 0)]
    public void Every_reads_as_the_cms_interval_type(string text, int type, int length)
    {
        Assert.True(JobIntervals.TryParse(text, out var parsedType, out var parsedLength, out var error), error);
        Assert.Equal((type, length), (parsedType, parsedLength));
    }

    [Theory]
    [InlineData("10s", "shorter than a minute")]
    [InlineData("1M", "ambiguous: give 1m for minutes or 1mo for months")]
    [InlineData("0h", "not an interval")]
    [InlineData("h", "not an interval")]
    [InlineData("1x", "not an interval")]
    [InlineData("-1h", "not an interval")]
    [InlineData("hourly", "not an interval")]
    public void Every_refuses_what_it_cant_take(string text, string reason)
    {
        Assert.False(JobIntervals.TryParse(text, out _, out _, out var error));
        Assert.Contains(reason, error);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(0, "unknown")]
    [InlineData(1, "succeeded")]
    [InlineData(2, "failed")]
    [InlineData(3, "cancelled")]
    [InlineData(4, "unableToStart")]
    [InlineData(5, "aborted")]
    [InlineData(9, "unknown")]
    public void Statuses_are_named_after_the_cms_enum(int? value, string name) => Assert.Equal(name, JobStatuses.Name(value));

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(1, "scheduler")]
    [InlineData(2, "user")]
    [InlineData(3, "restart")]
    public void Triggers_are_named_after_the_cms_enum(int? value, string name) => Assert.Equal(name, JobTriggers.Name(value));

    [Fact]
    public void Destructive_jobs_are_matched_by_full_class_name()
    {
        Assert.Contains("recycle bin", DestructiveJobs.Find("EPiServer.Util.EmptyWastebasketJob"));
        Assert.NotNull(DestructiveJobs.Find("EPiServer.Util.Internal.TrimContentVersionsJob"));
        Assert.NotNull(DestructiveJobs.Find("EPiServer.DataAbstraction.Activities.Internal.ActivityTruncateJob"));
        Assert.Contains("carts", DestructiveJobs.Find("EPiServer.Business.Commerce.ScheduledJobs.RemoveExpiredCartsJob"));
        Assert.Contains("archived catalog items", DestructiveJobs.Find("EPiServer.Business.Commerce.ScheduledJobs.ArchivedJob"));
        Assert.Null(DestructiveJobs.Find("Example.Jobs.EmptyWastebasketJob"));
        Assert.Null(DestructiveJobs.Find("EPiServer.Util.DelayedPublishJob"));
        Assert.Null(DestructiveJobs.Find(null));
    }

    [Fact]
    public void A_job_that_deletes_cant_be_undone_but_one_that_moves_content_can()
    {
        var trash = DestructiveJobs.Refusal("Automatic Emptying of Trash", "EPiServer.Util.EmptyWastebasketJob");
        var archive = DestructiveJobs.Refusal("Archive Function", "EPiServer.Util.PageArchiveJob");

        Assert.Contains("which can't be undone", trash);
        Assert.DoesNotContain("can't be undone", archive);
        Assert.Contains("moves every expired page to its archive page", archive);
        Assert.Null(DestructiveJobs.Refusal("Content import", "Example.Jobs.ImportJob"));
    }

    [Theory]
    [InlineData("EPiServer.Util.DelayedPublishJob", false)]
    [InlineData("EPiServer.LinkAnalyzer.LinkValidationJob", false)]
    [InlineData("EPiServer.Shell.Notification.RemoveInUseNotificationJob", false)]
    [InlineData("EPiServer.Cms.Shell.UI.Notifications.Feature.FeatureNotificationJob", false)]
    [InlineData("EPiServer.Business.Commerce.ScheduledJobs.PaymentPlanJob", true)]
    [InlineData("EPiServer.Business.Commerce.ScheduledJobs.RotateEncryptionJob", true)]
    [InlineData("EPiServer.Commerce.ODP.ODPExportJob", true)]
    [InlineData("EPiServer.Find.Cms.Job.IndexingJob", true)]
    [InlineData("Example.Jobs.ImportJob", true)]
    [InlineData(null, true)]
    public void Only_the_cms_core_jobs_run_without_a_warning(string? typeName, bool warns)
    {
        Assert.Equal(warns ? JobRequests.UnknownCodeWarning : null, JobRequests.Warning(typeName));
        Assert.Equal(!warns, CmsJobs.Contains(typeName));
    }

    [Fact]
    public void The_list_of_cms_jobs_has_every_destructive_cms_job()
    {
        Assert.All(DestructiveJobs.Classes.Where(c => !c.StartsWith("EPiServer.Business.Commerce.", StringComparison.Ordinal)), c => Assert.True(CmsJobs.Contains(c), c));
    }

    private static JobSchedule Schedule(bool enabled, int? nextInHours, string every = "1d")
    {
        JobIntervals.TryParse(every, out var type, out var length, out _);
        return new JobSchedule(enabled, nextInHours is { } hours ? Now.AddHours(hours) : null, type, length);
    }

    public static TheoryData<string, JobSchedule, JobSchedule, bool> Arming() => new()
    {
        { "enabled with a next run", Schedule(false, 5), Schedule(true, 5), true },
        { "given a next run", Schedule(true, null, "manual"), Schedule(true, 5), true },
        { "next run earlier", Schedule(true, 5), Schedule(true, 1), true },
        { "more often", Schedule(true, 5, "1w"), Schedule(true, 5, "1d"), true },
        { "next run later", Schedule(true, 1), Schedule(true, 5), false },
        { "less often", Schedule(true, 5, "1d"), Schedule(true, 5, "1w"), false },
        { "disabled", Schedule(true, 5), Schedule(false, 1), false },
        { "made manual", Schedule(true, 5), Schedule(true, null, "manual"), false },
        { "enabled without a next run", Schedule(false, null, "manual"), Schedule(true, null, "manual"), false },
    };

    [Theory]
    [MemberData(nameof(Arming))]
    public void A_change_arms_a_job_when_the_scheduler_will_run_it_newly_sooner_or_more_often(string change, JobSchedule before, JobSchedule after, bool arms) =>
        Assert.True(arms == DestructiveJobs.Arms(before, after), change);

    [Fact]
    public void Set_needs_the_flag_to_arm_a_destructive_job_and_never_to_disarm_one()
    {
        var trash = Job("Automatic Emptying of Trash", "EPiServer.Util.EmptyWastebasketJob", datePart: "wk", interval: 1, next: Now.AddDays(3), enabled: false);

        var enable = Assert.Throws<RefusedException>(() => JobRunner.RequireSettable(trash, false, true, null, null, allowDestructive: false));
        var sooner = Assert.Throws<RefusedException>(() => JobRunner.RequireSettable(trash with { Enabled = true }, false, null, null, Now, allowDestructive: false));

        Assert.Contains("permanently deletes the content in the recycle bin", enable.Message);
        Assert.Contains("--allow-destructive", sooner.Message);
        JobRunner.RequireSettable(trash, false, true, null, null, allowDestructive: true);
        JobRunner.RequireSettable(trash with { Enabled = true }, false, false, null, null, allowDestructive: false);
        JobRunner.RequireSettable(trash with { Enabled = true }, false, null, "manual", null, allowDestructive: false);
        JobRunner.RequireSettable(trash with { Enabled = true }, false, null, null, Now.AddDays(30), allowDestructive: false);
        JobRunner.RequireSettable(Job("Content import"), false, true, "1h", Now, allowDestructive: false);
        Assert.Throws<RefusedException>(() => JobRunner.RequireSettable(trash, true, false, null, null, allowDestructive: true));
    }

    [Theory]
    [InlineData("0b1c2d3e-0000-4000-8000-000000000001", "Content import")]
    [InlineData("content import", "Content import")]
    [InlineData("Example.Jobs.ExportJob", "Content export")]
    [InlineData("BlobCleanupJob", "Remove Abandoned BLOBs")]
    [InlineData("blobs", "Remove Abandoned BLOBs")]
    [InlineData("Search", "Search index")]
    public void A_job_is_found_by_id_name_class_or_a_unique_part_of_its_name(string reference, string name) =>
        Assert.Equal(name, JobReferences.Resolve(reference, Jobs).Name);

    [Fact]
    public void A_reference_several_jobs_match_is_a_usage_error_listing_them()
    {
        var name = Assert.Throws<UsageException>(() => JobReferences.Resolve("content", Jobs));
        var shortClass = Assert.Throws<UsageException>(() => JobReferences.Resolve("IndexJob", Jobs));

        Assert.Equal($"'content' matches 2 jobs: 'Content import' (Example.Jobs.ImportJob, {ImportId}), 'Content export' (Example.Jobs.ExportJob, {Jobs[1].Id}).", name.Message);
        Assert.Contains("'Search index' (Example.Search.IndexJob, ", shortClass.Message);
        Assert.Contains("'Other index' (Example.Other.IndexJob, ", shortClass.Message);
    }

    [Fact]
    public void An_unknown_job_is_not_found_with_close_names_and_one_only_in_the_code_says_so()
    {
        var sources = new[] { new ScheduledJobSource("Example.Jobs.NewJob", null, "New job", null, 0, 0, false, "Jobs/NewJob.cs", 7) };

        var typo = Assert.Throws<NotFoundException>(() => JobReferences.Resolve("Content imprt", Jobs));
        var unregistered = Assert.Throws<NotFoundException>(() => JobReferences.Resolve("NewJob", Jobs, sources));

        Assert.Contains("Did you mean Content import", typo.Hint);
        Assert.Equal("'New job' is in the code (Jobs/NewJob.cs:7) but not in the database yet.", unregistered.Message);
        Assert.Contains("opticli serve", unregistered.Hint);
    }

    [Fact]
    public void Jobs_list_their_schedule_overdue_state_last_run_source_and_whether_their_class_is_gone()
    {
        var rows = new[]
        {
            Job("Content import", "Example.Jobs.ImportJob", ImportId, "hh", 1, Now.AddHours(-2), lastStatus: 2, lastText: "<p>Failed:<br/>timeout &amp; retry</p>"),
            Job("Disabled", datePart: "dd", interval: 1, next: Now.AddDays(-1), enabled: false),
            Job("Stuck", running: true, ping: 600),
            Job("Busy", running: true, ping: 10),
            Job("Hidden", hidden: true),
        };
        var sources = new[]
        {
            new ScheduledJobSource("Example.Jobs.ImportJob", ImportId, "Content import", null, 5, 1, true, "Jobs/ImportJob.cs", 12),
            new ScheduledJobSource("Example.Jobs.NewJob", Guid.Parse("0b1c2d3e-0000-4000-8000-000000000009"), "New job", null, 4, 1, false, "Jobs/NewJob.cs", 7),
        };

        var views = JobViews.List(rows, sources, new HashSet<string> { "Example" }, Now, all: false);

        var import = views[0];
        Assert.Equal(("every 1 hour", true, "failed", "Failed: timeout & retry", "Jobs/ImportJob.cs:12"), (import.Schedule, import.Overdue, import.LastStatus, import.LastMessage, import.Source));
        Assert.Null(views[1].Overdue);
        // Of the site's own assembly, and not in the code: a job whose class was removed.
        Assert.Equal(((bool?)null, (bool?)false), (import.InCode, views[1].InCode));
        Assert.Equal("stale", views[2].Running);
        Assert.Equal(true, views[3].Running);
        Assert.DoesNotContain(views, v => v.Name == "Hidden");
        var unregistered = views[^1];
        Assert.Equal(("New job", "every 1 day", (bool?)false, "Jobs/NewJob.cs:7"), (unregistered.Name, unregistered.Schedule, unregistered.Registered, unregistered.Source));
        Assert.Contains(JobViews.List(rows, sources, new HashSet<string>(), Now, all: true), v => v is { Name: "Hidden", Hidden: true });
    }

    [Fact]
    public void A_log_row_starts_its_duration_before_it_ended()
    {
        var finished = new DateTime(2026, 10, 6, 3, 0, 5, DateTimeKind.Utc);
        var row = new JobLogRow(7, ImportId, "Content import", finished, TimeSpan.FromMilliseconds(1240), 1, 1, "web01", "<b>Done</b>");

        var view = JobViews.From(row);

        Assert.Equal((finished.AddMilliseconds(-1240), "1.2 s", 1240L, "succeeded", "scheduler", "<b>Done</b>"), (view.Started, view.Duration, view.DurationMs, view.Status, view.Trigger, view.Message));
        Assert.Null(JobViews.From(row with { Duration = null }).Started);
        // Rounded to the millisecond, as the database's own times are.
        Assert.Equal(finished.AddMilliseconds(-1000), JobViews.From(row with { Duration = TimeSpan.FromTicks(10_000_009) }).Started);
    }

    [Theory]
    [InlineData(400, "0.4 s")]
    [InlineData(59_900, "59.9 s")]
    [InlineData(125_000, "2 min 5 s")]
    [InlineData(3_780_000, "1 h 3 min")]
    public void Durations_read_in_the_largest_fitting_unit(int milliseconds, string text) =>
        Assert.Equal(text, JobViews.Duration(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void A_long_message_is_cut_to_one_line()
    {
        var line = JobViews.OneLine("First\r\nsecond\t" + new string('x', 400));

        Assert.StartsWith("First second x", line);
        Assert.Equal(200, line!.Length);
        Assert.EndsWith("…", line);
        Assert.Null(JobViews.OneLine("  "));
    }

    [Theory]
    [InlineData("30m", "2026-10-06T11:30:00Z")]
    [InlineData("12h", "2026-10-06T00:00:00Z")]
    [InlineData("7d", "2026-09-29T12:00:00Z")]
    [InlineData("2w", "2026-09-22T12:00:00Z")]
    [InlineData("2026-10-01", "2026-10-01T00:00:00Z")]
    [InlineData("2026-10-01T08:00:00+02:00", "2026-10-01T06:00:00Z")]
    public void Since_takes_an_age_or_a_utc_date(string text, string expected) =>
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal), JobTimes.ParseSince(text, Now));

    [Fact]
    public void Next_takes_now_or_a_time_and_allows_one_that_has_passed()
    {
        Assert.Equal(Now, JobTimes.ParseNext("now", Now));
        Assert.Equal(new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc), JobTimes.ParseNext("2026-10-07T03:00+02:00", Now));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), JobTimes.ParseNext("2026-01-01T00:00Z", Now));
        Assert.Throws<UsageException>(() => JobTimes.ParseNext("tomorrow", Now));
        Assert.Throws<UsageException>(() => JobTimes.ParseSince("7x", Now));
    }

    [Fact]
    public void The_user_config_can_turn_the_scheduler_on_for_serve()
    {
        using var root = new TempDirectory();
        var file = root.Write("config.json", """{"projects": {"/sites/a": {"scheduler": true}, "/sites/b": {"https": true}}}""");

        Assert.True(UserConfig.ForProject(file, "/sites/a")!.Scheduler);
        Assert.False(UserConfig.ForProject(file, "/sites/b")!.Scheduler);
    }
}
