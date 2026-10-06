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

    /// <summary>The next run (UTC), or null to leave it. <see cref="JobSetResult"/> says what it became.</summary>
    public DateTime? Next { get; init; }

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

    /// <summary>The warning a run of a job that isn't one of the CMS's own carries.</summary>
    public const string CustomJobWarning = "runs the site's own code; opticli can't tell what it changes";
}

/// <summary>
/// The CMS's built-in jobs that delete (or move) data for good, by class name (<c>tblScheduledItem.TypeName</c>), with
/// what each deletes. <c>jobs run</c> refuses them without <c>--allow-destructive</c>; the agent decides, and the CLI
/// checks first. Names checked by reflection over EPiServer.dll (CMS 12).
/// </summary>
public static class DestructiveJobs
{
    private static readonly Dictionary<string, string> Jobs = new(StringComparer.Ordinal)
    {
        ["EPiServer.Util.EmptyWastebasketJob"] = "permanently deletes the content in the recycle bin",
        ["EPiServer.Util.BlobCleanupJob"] = "deletes media files (blobs) no content refers to",
        ["EPiServer.Util.Internal.TrimContentVersionsJob"] = "deletes old content versions",
        ["EPiServer.Util.CleanUnusedAssetsFoldersJob"] = "deletes content asset folders whose content is gone",
        ["EPiServer.DataAbstraction.Activities.Internal.ActivityTruncateJob"] = "deletes old change log entries (the activity log)",
        ["EPiServer.Notification.Internal.NotificationMessageTruncateJob"] = "deletes old notification messages",
        ["EPiServer.Util.TaskMonitorTruncateJob"] = "deletes old monitored task entries",
        ["EPiServer.Util.PageArchiveJob"] = "moves expired content to its archive page",
    };

    /// <summary>What the job deletes, or null when it isn't one of these.</summary>
    public static string? Find(string? typeName) => typeName is not null && Jobs.TryGetValue(typeName, out var what) ? what : null;

    public static IReadOnlyCollection<string> Classes => Jobs.Keys;

    public static string Refusal(string name, string what) =>
        $"'{name}' {what}, which can't be undone. Run it with --allow-destructive if that is what you want.";

    public const string Hint =
        "--allow-destructive runs it; it is never implied by --yes or a prompt. Ask the user first: a restored database's recycle bin, versions and change log are often the only copy.";

    /// <summary>
    /// Whether the job is one of the CMS's own (or an add-on's from Optimizely), whose effects are documented, rather than
    /// the site's own code.
    /// </summary>
    public static bool IsBuiltIn(string? typeName) => typeName is not null && typeName.StartsWith("EPiServer.", StringComparison.Ordinal);
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
        var value = text.Trim().ToLowerInvariant();
        if (value == Manual)
        {
            return true;
        }
        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }
        var suffix = value[digits..];
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
