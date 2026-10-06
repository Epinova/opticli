using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Core.Jobs;

/// <summary>One row of <c>opticli jobs</c>.</summary>
/// <param name="Id">The job's id (<c>tblScheduledItem.pkID</c>); for a job only in code, its attribute's GUID (null without one).</param>
/// <param name="Schedule"><c>every 1 hour</c>, or <c>manual</c>.</param>
/// <param name="NextRun">UTC; null when the scheduler never starts it.</param>
/// <param name="Overdue">Enabled with a next run that has passed: the scheduler starts it as soon as it runs.</param>
/// <param name="LastStatus">One of <see cref="JobStatuses"/>; null before the first run.</param>
/// <param name="LastMessage">The last run's message, as one line of plain text (<c>jobs log</c> has it whole).</param>
/// <param name="Running"><c>true</c>, <c>false</c>, or <c>"stale"</c>: marked running, but the process that ran it stopped pinging.</param>
/// <param name="Class">The job's class (<c>TypeName</c>).</param>
/// <param name="Source">The class's file and line in the site's source; null for a job from a package.</param>
/// <param name="Registered">False for a job in the code that the database doesn't have yet; omitted otherwise.</param>
/// <param name="InCode">
/// False for a job of one of the site's own assemblies whose class isn't in the code any more: the CMS leaves a removed
/// job's row behind, and a run of it can't start. Omitted otherwise.
/// </param>
public sealed record JobView(
    Guid? Id,
    string Name,
    bool Enabled,
    string Schedule,
    DateTime? NextRun,
    bool? Overdue,
    DateTime? LastRun,
    string? LastStatus,
    string? LastMessage,
    object Running,
    bool Stoppable,
    string? Class,
    string? Source,
    bool? Hidden = null,
    bool? Registered = null,
    bool? InCode = null);

/// <summary>One run in <c>opticli jobs log</c>, and the end of <c>jobs run</c>.</summary>
/// <param name="Started">When it started (UTC): when it ended less its duration.</param>
/// <param name="Finished">When it ended (UTC): the CMS logs a run as it ends.</param>
/// <param name="Duration">Readable: <c>1.2 s</c>, <c>3 min 4 s</c>.</param>
/// <param name="Status">One of <see cref="JobStatuses"/>.</param>
/// <param name="Trigger"><c>scheduler</c>, <c>user</c> (admin UI or <c>jobs run</c>), <c>restart</c> (after its process died), <c>unknown</c>.</param>
/// <param name="Message">The job's message as it returned it; it can be HTML.</param>
public sealed record JobLogView(
    string? Job,
    Guid JobId,
    DateTime? Started,
    DateTime Finished,
    string? Duration,
    long? DurationMs,
    string Status,
    string Trigger,
    string? Server,
    string? Message);

/// <summary>Turns the job tables' rows into what <c>jobs</c> and <c>jobs log</c> print.</summary>
public static partial class JobViews
{
    /// <summary>
    /// How long a job may go without a ping before it counts as stale: the CMS's own threshold, 4 pings at its default
    /// <c>SchedulerOptions.PingTime</c> of 30 s.
    /// </summary>
    public const int StaleSeconds = 4 * 30;

    private const int MessageLength = 200;

    /// <summary>Every job in the database, then every job in the code the database doesn't have.</summary>
    /// <param name="assemblies">The assemblies the site's own projects build (<see cref="ScheduledJobSources.Assemblies"/>).</param>
    /// <param name="all">Include jobs the CMS hides from the admin UI.</param>
    public static IReadOnlyList<JobView> List(IReadOnlyList<JobRow> rows, IReadOnlyList<ScheduledJobSource> sources, IReadOnlySet<string> assemblies, DateTime now, bool all)
    {
        var views = rows.Where(r => all || !r.Hidden).Select(r =>
        {
            var source = ScheduledJobSources.Match(sources, r.Id, r.TypeName);
            var view = From(r, source, now);
            return source is null && r.AssemblyName is { } assembly && assemblies.Contains(assembly) ? view with { InCode = false } : view;
        }).ToList();
        foreach (var source in sources.Where(s => !rows.Any(r => (s.Guid is { } guid && r.Id == guid) || r.TypeName == s.TypeName)))
        {
            views.Add(new JobView(
                source.Guid,
                source.DisplayName ?? source.TypeName,
                true,
                JobIntervals.Describe(source.IntervalType, source.IntervalLength),
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                source.TypeName,
                Location(source),
                Registered: false));
        }
        return views;
    }

    public static JobView From(JobRow row, ScheduledJobSource? source, DateTime now) => new(
        row.Id,
        row.Name,
        row.Enabled,
        Schedule(row),
        row.NextRun,
        row.Enabled && row.NextRun is { } next && next < now ? true : null,
        row.LastRun,
        row.LastStatus is null ? null : JobStatuses.Name(row.LastStatus),
        OneLine(row.LastText),
        IsStale(row) ? "stale" : row.Running,
        row.Stoppable,
        row.TypeName,
        source is null ? null : Location(source),
        row.Hidden ? true : null);

    public static JobLogView From(JobLogRow row) => new(
        row.JobName,
        row.JobId,
        row.Duration is { } duration ? row.Finished - duration : null,
        row.Finished,
        row.Duration is { } length ? Duration(length) : null,
        row.Duration is { } ms ? (long)ms.TotalMilliseconds : null,
        JobStatuses.Name(row.Status),
        JobTriggers.Name(row.Trigger),
        row.Server,
        row.Text);

    public static string Schedule(JobRow row) => JobIntervals.Describe(JobIntervals.TypeOf(row.DatePart), row.Interval);

    /// <summary>Marked running, but whatever ran it stopped pinging: its process died, or it runs on another server whose clock is off.</summary>
    public static bool IsStale(JobRow row) => row.Running && row.SecondsSincePing is { } seconds && seconds >= StaleSeconds;

    /// <summary>Running in a process that still pings.</summary>
    public static bool IsRunning(JobRow row) => row.Running && !IsStale(row);

    /// <summary><c>0.4 s</c>, <c>12.3 s</c>, <c>2 min 5 s</c>, <c>1 h 3 min</c>.</summary>
    public static string Duration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 60 => $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s",
        < 3600 => $"{(int)duration.TotalMinutes} min {duration.Seconds} s",
        _ => $"{(int)duration.TotalHours} h {duration.Minutes} min",
    };

    /// <summary>The message as one line of plain text: tags removed, entities decoded, whitespace collapsed, cut at 200 characters.</summary>
    public static string? OneLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }
        var text = WebUtility.HtmlDecode(BreakPattern().Replace(TagPattern().Replace(BreakTagPattern().Replace(message, " "), ""), " ")).Trim();
        text = SpacePattern().Replace(text, " ");
        return text.Length <= MessageLength ? text : text[..(MessageLength - 1)].TrimEnd() + "…";
    }

    private static string Location(ScheduledJobSource source) => $"{source.File.Replace('\\', '/')}:{source.Line}";

    [GeneratedRegex(@"<\s*(?:br|/p|/div|/li)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTagPattern();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[\r\n\t]+")]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex SpacePattern();
}
