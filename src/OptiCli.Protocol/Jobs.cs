using System.Globalization;

namespace OptiCli.Protocol;

/// <summary>
/// Body of <see cref="AgentRoutes.JobRun"/>: start a scheduled job now, as the admin UI's "Start manually" does
/// (<c>IScheduledJobExecutor.StartAsync</c> with a user trigger). The agent returns once the job has started; the CLI
/// waits for it by reading <c>tblScheduledItem</c> and <c>tblScheduledItemLog</c>.
/// </summary>
public sealed record JobRunRequest
{
    /// <summary>The job's id (<c>tblScheduledItem.pkID</c>).</summary>
    public required Guid Job { get; init; }

    /// <summary>Run a job of <see cref="DestructiveJobs"/>; without it such a job is refused.</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Check that the job exists and would be started, without starting it.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.JobRun"/>.</summary>
public sealed record JobRunResult
{
    /// <summary>The job as it was when it was started.</summary>
    public required JobState Job { get; init; }

    public bool Started { get; init; }

    public bool DryRun { get; init; }

    /// <summary>When the agent asked the CMS to start it (UTC).</summary>
    public DateTime? Since { get; init; }

    /// <summary>The job runs the site's own code, or was marked running by a process that stopped, ...</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Body of <see cref="AgentRoutes.JobStop"/>.</summary>
public sealed record JobStopRequest
{
    public required Guid Job { get; init; }

    /// <summary>Check that the job runs here and can be stopped, without stopping it.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.JobStop"/>.</summary>
/// <param name="Stopping">The job was asked to stop; it ends once its own code notices. False for a dry run.</param>
public sealed record JobStopResult(JobState Job, bool Stopping, bool DryRun = false);

/// <summary>
/// Body of <see cref="AgentRoutes.JobSet"/>: change a job's schedule the way the admin UI's form does
/// (<c>IScheduledJobRepository.Save</c>), so the scheduler sees it. Fields left out stay as they are.
/// </summary>
public sealed record JobSetRequest
{
    public required Guid Job { get; init; }

    public bool? Enabled { get; init; }

    /// <summary>The interval, <see cref="JobIntervals.Syntax"/>: <c>30m</c>, <c>1h</c>, <c>1d</c>, <c>1w</c>, <c>1mo</c>, <c>1y</c>, or <c>manual</c>.</summary>
    public string? Every { get; init; }

    /// <summary>The next run, or null to leave it; a time without an offset is UTC. <see cref="JobSetResult"/> says what it became.</summary>
    public DateTime? Next { get; init; }

    /// <summary>Allow a change that arms a <see cref="DestructiveJobs"/> job (<see cref="DestructiveJobs.Arms"/>).</summary>
    public bool AllowDestructive { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.JobSet"/>. Schedules aren't versioned: <see cref="Before"/> is the only record.</summary>
/// <param name="Changes">One line per changed setting; empty when nothing changed (and nothing was saved).</param>
public sealed record JobSetResult(JobState Before, JobState After, IReadOnlyList<string> Changes, bool DryRun, bool Saved)
{
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>A job as the CMS's job repository has it.</summary>
/// <param name="Class">The job's class (<c>TypeName</c>).</param>
/// <param name="Every">The interval in <see cref="JobIntervals.Syntax"/>, null for a manual job.</param>
/// <param name="Schedule"><see cref="JobIntervals.Describe"/>: <c>every 1 hour</c>, <c>manual</c>.</param>
/// <param name="NextRun">UTC; null when the scheduler never starts it.</param>
public sealed record JobState(Guid Id, string Name, string? Class, bool Enabled, string Schedule, string? Every, DateTime? NextRun, bool Running, bool Stoppable);

/// <summary>Why jobs aren't run, stopped or changed against a shared database, for both sides' refusals.</summary>
public static class JobRequests
{
    public const string SharedRefusal =
        "opticli serve runs against a shared database here: the deployed site's scheduler uses the same jobs, and a job writes to the content everyone shares.";

    public const string SharedHint =
        "Run, stop or reschedule jobs on a shared database in that environment's own admin UI. `opticli jobs` and `opticli jobs log` still read them.";

    /// <summary>The warning a run of a job that isn't one of <see cref="CmsJobs"/> carries: the site's own, or an add-on's.</summary>
    public const string UnknownCodeWarning = "runs code opticli doesn't know: it may change content or contact external systems";

    /// <summary>What a run of the job warns about; null for the CMS's own jobs.</summary>
    public static string? Warning(string? typeName) => CmsJobs.Contains(typeName) ? null : UnknownCodeWarning;
}

/// <summary>
/// The jobs of the CMS itself (EPiServer.CMS.Core, EPiServer.LinkAnalyzer, EPiServer.UI, EPiServer.Cms.Shell.UI), by
/// class name: what they do is known. Checked by reflection over the CMS 12.0 and 12.21 assemblies; every other job,
/// add-ons from Optimizely (Commerce, Find, Forms, ...) included, is code opticli doesn't know.
/// </summary>
public static class CmsJobs
{
    private static readonly HashSet<string> Classes = new(StringComparer.Ordinal)
    {
        "EPiServer.Util.BlobCleanupJob",
        "EPiServer.Util.CleanUnusedAssetsFoldersJob",
        "EPiServer.Util.DelayedPublishJob",
        "EPiServer.Util.EmptyWastebasketJob",
        "EPiServer.Util.PageArchiveJob",
        "EPiServer.Util.TaskMonitorTruncateJob",
        "EPiServer.Util.ThumbnailPropertiesClearJob",
        "EPiServer.Util.Internal.TrimContentVersionsJob",
        "EPiServer.Notification.Internal.NotificationDispatcherJob",
        "EPiServer.Notification.Internal.NotificationMessageTruncateJob",
        "EPiServer.DataAbstraction.Activities.Internal.ActivityTruncateJob",
        "EPiServer.LinkAnalyzer.LinkValidationJob",
        "EPiServer.Shell.Notification.RemoveInUseNotificationJob",
        "EPiServer.Cms.Shell.UI.Notifications.Feature.FeatureNotificationJob",
    };

    public static bool Contains(string? typeName) => typeName is not null && Classes.Contains(typeName);
}

/// <summary>
/// Jobs that delete data for good, or change content across the site, by class name (<c>tblScheduledItem.TypeName</c>),
/// with what each does. <c>jobs run</c> refuses them, and <c>jobs set</c> refuses to arm them (enable them with a next
/// run, or bring their next run or interval closer), without <c>--allow-destructive</c>; the agent decides, and the CLI
/// checks first. The CMS's from reflection over EPiServer.dll (CMS 12); Commerce's from EPiServer.Business.Commerce
/// 14.x, whose code was read to see what each deletes.
/// </summary>
public static class DestructiveJobs
{
    /// <param name="Deletes">It deletes for good; otherwise it changes content in a way that can be undone by hand.</param>
    private sealed record Effect(string What, bool Deletes);

    private static readonly Dictionary<string, Effect> Jobs = new(StringComparer.Ordinal)
    {
        ["EPiServer.Util.EmptyWastebasketJob"] = new("permanently deletes the content in the recycle bin", true),
        ["EPiServer.Util.BlobCleanupJob"] = new("deletes media files (blobs) no content refers to", true),
        ["EPiServer.Util.Internal.TrimContentVersionsJob"] = new("deletes old content versions", true),
        ["EPiServer.Util.CleanUnusedAssetsFoldersJob"] = new("deletes content asset folders whose content is gone", true),
        ["EPiServer.DataAbstraction.Activities.Internal.ActivityTruncateJob"] = new("deletes old change log entries (the activity log)", true),
        ["EPiServer.Notification.Internal.NotificationMessageTruncateJob"] = new("deletes old notification messages", true),
        ["EPiServer.Util.TaskMonitorTruncateJob"] = new("deletes old monitored task entries", true),
        ["EPiServer.Util.PageArchiveJob"] = new("moves every expired page to its archive page", false),
        ["EPiServer.Business.Commerce.ScheduledJobs.RemoveExpiredCartsJob"] = new("deletes carts older than its threshold (30 days by default)", true),
        ["EPiServer.Business.Commerce.ScheduledJobs.ArchivedJob"] = new("permanently deletes archived catalog items older than their threshold", true),
        ["EPiServer.Business.Commerce.ScheduledJobs.RemoveExpiredLowestPriceJob"] = new("deletes lowest-price history older than its threshold (30 days by default)", true),
    };

    /// <summary>What the job does, or null when it isn't one of these.</summary>
    public static string? Find(string? typeName) => typeName is not null && Jobs.TryGetValue(typeName, out var effect) ? effect.What : null;

    public static IReadOnlyCollection<string> Classes => Jobs.Keys;

    /// <summary>Why <c>jobs run</c> refuses it; null when it isn't one of these.</summary>
    public static string? Refusal(string name, string? typeName) => typeName is not null && Jobs.TryGetValue(typeName, out var effect)
        ? effect.Deletes
            ? $"'{name}' {effect.What}, which can't be undone. Run it with --allow-destructive if that is what you want."
            : $"'{name}' {effect.What}, changing content across the site (each move can be undone by hand, e.g. `opticli move`). Run it with --allow-destructive if that is what you want."
        : null;

    /// <summary>Why <c>jobs set</c> refuses a change that arms it; null when it isn't one of these.</summary>
    public static string? SetRefusal(string name, string? typeName) => Find(typeName) is { } what
        ? $"This change lets the scheduler run '{name}' (or run it sooner), and it {what}. Give --allow-destructive if that is what you want."
        : null;

    public const string Hint =
        "--allow-destructive allows it; it is never implied by --yes or a prompt. Ask the user first: a restored database's recycle bin, versions and change log are often the only copy.";

    /// <summary>
    /// Whether going from <paramref name="before"/> to <paramref name="after"/> arms the job: the scheduler will start it
    /// (enabled, with a next run) and didn't, or will start it sooner (an earlier next run) or more often (a shorter
    /// interval). Disabling it, or making it manual, never does.
    /// </summary>
    public static bool Arms(JobSchedule before, JobSchedule after)
    {
        if (!after.Armed)
        {
            return false;
        }
        if (!before.Armed)
        {
            return true;
        }
        return after.NextRun < before.NextRun
            || (JobIntervals.Approximate(after.IntervalType, after.IntervalLength) is { } shorter
                && (JobIntervals.Approximate(before.IntervalType, before.IntervalLength) is not { } longer || shorter < longer));
    }
}

/// <summary>What decides when the scheduler starts a job.</summary>
/// <param name="IntervalType">The <c>ScheduledIntervalType</c> value; 0 for a manual job.</param>
/// <param name="NextRun">UTC; null when it has none.</param>
public sealed record JobSchedule(bool Enabled, DateTime? NextRun, int IntervalType, int IntervalLength)
{
    /// <summary>The scheduler starts it at its next run.</summary>
    public bool Armed => Enabled && NextRun is not null;
}

/// <summary>
/// Job intervals: the CMS's <c>ScheduledIntervalType</c> (by value: None, Years, Months, Weeks, Days, Hours, Minutes,
/// Seconds), its <c>tblScheduledItem.DatePart</c> code (<c>yy</c>, <c>mm</c>, <c>wk</c>, <c>dd</c>, <c>hh</c>, <c>mi</c>,
/// <c>ss</c>; NULL for none), and the CLI's <c>--every</c> units.
/// </summary>
public static class JobIntervals
{
    public const string Manual = "manual";

    public const string Syntax = "<n><unit> with unit m (minutes), h, d, w, mo (months) or y, e.g. 30m, 1h, 1d; or manual";

    /// <param name="Type">The <c>ScheduledIntervalType</c> value.</param>
    /// <param name="DatePart">The DatePart code.</param>
    /// <param name="Suffix">The <c>--every</c> unit.</param>
    private sealed record Unit(int Type, string DatePart, string Suffix, string Singular, string Plural);

    /// <summary>By <c>ScheduledIntervalType</c> value; 0 (None) has no unit.</summary>
    private static readonly Unit[] Units =
    [
        new(1, "yy", "y", "year", "years"),
        new(2, "mm", "mo", "month", "months"),
        new(3, "wk", "w", "week", "weeks"),
        new(4, "dd", "d", "day", "days"),
        new(5, "hh", "h", "hour", "hours"),
        new(6, "mi", "m", "minute", "minutes"),
        new(7, "ss", "s", "second", "seconds"),
    ];

    /// <summary>The <c>ScheduledIntervalType</c> value of a DatePart code; 0 for NULL or an unknown code, as the CMS reads them.</summary>
    public static int TypeOf(string? datePart) => Units.FirstOrDefault(u => u.DatePart == datePart?.Trim())?.Type ?? 0;

    /// <summary><c>every 1 hour</c>, <c>every 30 minutes</c>, or <c>manual</c> for no interval.</summary>
    public static string Describe(int type, int length) =>
        Find(type, length) is { } unit ? $"every {length} {(length == 1 ? unit.Singular : unit.Plural)}" : Manual;

    /// <summary>The <c>--every</c> form (<c>1h</c>), or null for no interval.</summary>
    public static string? Short(int type, int length) => Find(type, length) is { } unit ? $"{length}{unit.Suffix}" : null;

    /// <summary>Parses <c>--every</c>.</summary>
    /// <param name="type">The <c>ScheduledIntervalType</c> value; 0 for <see cref="Manual"/>.</param>
    /// <param name="error">Why it isn't one, for a usage error.</param>
    public static bool TryParse(string text, out int type, out int length, out string? error)
    {
        type = 0;
        length = 0;
        error = null;
        var value = text.Trim();
        if (value.Equals(Manual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }
        // Case doesn't matter, except for a lone M: minutes (m) or months (M, as in .NET's format strings)?
        if (value[digits..] == "M")
        {
            error = $"'{text}' is ambiguous: give {value[..digits]}m for minutes or {value[..digits]}mo for months.";
            return false;
        }
        var suffix = value[digits..].ToLowerInvariant();
        var unit = Units.FirstOrDefault(u => u.Suffix == suffix || u.Singular == suffix || u.Plural == suffix);
        if (digits == 0 || unit is null || !int.TryParse(value[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length <= 0)
        {
            error = $"'{text}' is not an interval: give {Syntax}.";
            return false;
        }
        if (unit.Type == 7)
        {
            error = $"'{text}' is shorter than a minute; the CMS takes seconds, but a job that often isn't something to set from here.";
            return false;
        }
        type = unit.Type;
        return true;
    }

    /// <summary>The interval's length, a month taken as 30 days and a year as 365; null for no interval.</summary>
    public static TimeSpan? Approximate(int type, int length) => Find(type, length) is { } unit
        ? unit.Type switch
        {
            1 => TimeSpan.FromDays(365 * length),
            2 => TimeSpan.FromDays(30 * length),
            3 => TimeSpan.FromDays(7 * length),
            4 => TimeSpan.FromDays(length),
            5 => TimeSpan.FromHours(length),
            6 => TimeSpan.FromMinutes(length),
            _ => TimeSpan.FromSeconds(length),
        }
        : null;

    private static Unit? Find(int type, int length) => length > 0 ? Units.FirstOrDefault(u => u.Type == type) : null;
}

/// <summary>
/// Names of the CMS's <c>ScheduledJobExecutionStatus</c> (<c>tblScheduledItem.LastStatus</c>,
/// <c>tblScheduledItemLog.Status</c>), by value.
/// </summary>
public static class JobStatuses
{
    public const string Unknown = "unknown";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>Stopped by a user (admin UI's Stop, <c>opticli jobs stop</c>).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The CMS couldn't create the job (its class is gone, its constructor threw).</summary>
    public const string UnableToStart = "unableToStart";

    /// <summary>Stopped because the site shut down.</summary>
    public const string Aborted = "aborted";

    private static readonly string[] ByValue = [Unknown, Succeeded, Failed, Cancelled, UnableToStart, Aborted];

    public static string Name(int? value) => value is { } v && v >= 0 && v < ByValue.Length ? ByValue[v] : Unknown;

    /// <summary>The values <c>--failed</c> selects: the job didn't finish its work, and nobody asked it to stop.</summary>
    public static readonly IReadOnlyList<int> FailedValues = [2, 4, 5];
}

/// <summary>Names of the CMS's <c>ScheduledJobTrigger</c> (<c>tblScheduledItemLog.Trigger</c>), by value.</summary>
public static class JobTriggers
{
    private static readonly string[] ByValue = ["unknown", "scheduler", "user", "restart"];

    public static string Name(int? value) => value is { } v && v >= 0 && v < ByValue.Length ? ByValue[v] : "unknown";
}
