using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;
using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Jobs;

/// <summary>A row of <c>tblScheduledItem</c>: a scheduled job as the CMS registered it.</summary>
/// <param name="LastStatus">The last run's <c>ScheduledJobExecutionStatus</c> value (<see cref="Protocol.JobStatuses"/>); null before the first.</param>
/// <param name="DatePart">The interval's unit code (<c>mi</c>, <c>hh</c>, ...; <see cref="Protocol.JobIntervals"/>), null for a manual job.</param>
/// <param name="SecondsSincePing">Seconds since the process running it last said so (<c>LastPing</c>); null when it never ran.</param>
/// <param name="Hidden">Left out of the admin UI's list (CMS 12.x versions without the column: false).</param>
public sealed record JobRow(
    Guid Id,
    string Name,
    bool Enabled,
    DateTime? LastRun,
    int? LastStatus,
    string? LastText,
    DateTime? NextRun,
    string? DatePart,
    int Interval,
    string? TypeName,
    string? AssemblyName,
    bool Running,
    string? StatusMessage,
    int? SecondsSincePing,
    bool Stoppable,
    bool Restartable,
    bool Hidden);

/// <summary>A row of <c>tblScheduledItemLog</c>: one run of a job.</summary>
/// <param name="Id">The row's id; a later run has a higher one.</param>
/// <param name="Finished">When the run ended (<c>Exec</c>, UTC): the CMS writes the row as the job returns.</param>
/// <param name="Duration">The run's length (<c>Duration</c>, stored as ticks); null when the CMS didn't say.</param>
/// <param name="Status">The <c>ScheduledJobExecutionStatus</c> value.</param>
/// <param name="Trigger">The <c>ScheduledJobTrigger</c> value.</param>
public sealed record JobLogRow(long Id, Guid JobId, string? JobName, DateTime Finished, TimeSpan? Duration, int? Status, int? Trigger, string? Server, string? Text);

/// <summary>What <c>jobs log</c> selects.</summary>
/// <param name="Job">One job's runs; null for every job's.</param>
/// <param name="FailedOnly">Only runs that failed, couldn't start or were aborted (<see cref="Protocol.JobStatuses.FailedValues"/>).</param>
/// <param name="Since">Only runs that ended on or after this (UTC).</param>
public sealed record JobLogQuery(Guid? Job = null, bool FailedOnly = false, DateTime? Since = null);

/// <summary>
/// Fixed queries over the CMS's scheduled job tables, which hold no personal data: the job's settings and state, and the
/// log of its runs (status, message, server name).
/// </summary>
public static class JobReader
{
    private const string Columns = """
        pkID, Name, CONVERT(bit, Enabled) AS Enabled, LastExec, LastStatus, LastText, NextExec,
        RTRIM([DatePart]) AS [DatePart], ISNULL(Interval, 0) AS Interval, TypeName, AssemblyName,
        CONVERT(bit, ISNULL(IsRunning, 0)) AS IsRunning, CurrentStatusMessage, DATEDIFF(second, LastPing, GETUTCDATE()) AS SecondsSincePing,
        CONVERT(bit, ISNULL(IsStoppable, 0)) AS IsStoppable, CONVERT(bit, ISNULL(Restartable, 0)) AS Restartable
        """;

    /// <summary><c>Hidden</c> came in a later CMS 12 version than the rest.</summary>
    private const string HasHiddenSql = "SELECT CASE WHEN COL_LENGTH(N'dbo.tblScheduledItem', N'Hidden') IS NULL THEN 0 ELSE 1 END";

    private const string ListSql = $"SELECT {Columns}, CONVERT(bit, 0) AS Hidden FROM tblScheduledItem ORDER BY Name";

    private const string ListWithHiddenSql = $"SELECT {Columns}, CONVERT(bit, ISNULL(Hidden, 0)) AS Hidden FROM tblScheduledItem ORDER BY Name";

    private const string LogSql = """
        SELECT l.pkID, l.fkScheduledItemId, j.Name, l.[Exec], l.Duration, l.Status, l.[Trigger], l.Server, l.[Text]
        FROM tblScheduledItemLog l
        LEFT JOIN tblScheduledItem j ON j.pkID = l.fkScheduledItemId
        WHERE (@job IS NULL OR l.fkScheduledItemId = @job)
          AND (@failed = 0 OR l.Status IN (2, 4, 5))
          AND (@since IS NULL OR l.[Exec] >= @since)
        ORDER BY l.[Exec] DESC, l.pkID DESC
        OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY
        """;

    private const string LatestLogIdSql = "SELECT ISNULL(MAX(pkID), 0) AS Latest FROM tblScheduledItemLog WHERE fkScheduledItemId = @job";

    private const string LogAfterSql = """
        SELECT TOP 1 l.pkID, l.fkScheduledItemId, j.Name, l.[Exec], l.Duration, l.Status, l.[Trigger], l.Server, l.[Text]
        FROM tblScheduledItemLog l
        LEFT JOIN tblScheduledItem j ON j.pkID = l.fkScheduledItemId
        WHERE l.fkScheduledItemId = @job AND l.pkID > @after
        ORDER BY l.pkID
        """;

    /// <summary>Every job, by name; on CMS 13 with the name admin mode shows rather than the class name it stores (<see cref="JobNames"/>).</summary>
    /// <param name="sources">The jobs in the site's source, for the names of the site's own jobs on CMS 13; null when not scanned.</param>
    public static async Task<IReadOnlyList<JobRow>> ListAsync(CmsDatabase db, CancellationToken cancellationToken, IReadOnlyList<ScheduledJobSource>? sources = null)
    {
        var hasHidden = (await db.QueryAsync(HasHiddenSql, r => r.GetInt32(0), cancellationToken)).Single() == 1;
        var rows = await db.QueryAsync(hasHidden ? ListWithHiddenSql : ListSql, ReadJob, cancellationToken);
        if ((await db.SchemaAsync(cancellationToken)).Major < 13)
        {
            return rows;
        }
        return rows
            .Select(r => r with { Name = JobNames.Readable(r.Id, r.Name, r.TypeName, sources) })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <returns>Null when there is no such job.</returns>
    public static async Task<JobRow?> GetAsync(CmsDatabase db, Guid id, CancellationToken cancellationToken, IReadOnlyList<ScheduledJobSource>? sources = null) =>
        (await ListAsync(db, cancellationToken, sources)).FirstOrDefault(j => j.Id == id);

    /// <summary>Runs, the latest first.</summary>
    /// <param name="take">Rows to read; pass the page size + 1 to know whether there are more.</param>
    /// <param name="sources">As for <see cref="ListAsync"/>: the names of the site's own jobs on CMS 13.</param>
    public static async Task<IReadOnlyList<JobLogRow>> LogAsync(CmsDatabase db, JobLogQuery query, int offset, int take, CancellationToken cancellationToken, IReadOnlyList<ScheduledJobSource>? sources = null) =>
        await ReadableAsync(db, await db.QueryAsync(LogSql, ReadLog, cancellationToken,
            new SqlParameter("@job", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)query.Job ?? DBNull.Value },
            new SqlParameter("@failed", System.Data.SqlDbType.Bit) { Value = query.FailedOnly },
            new SqlParameter("@since", System.Data.SqlDbType.DateTime) { Value = (object?)query.Since ?? DBNull.Value },
            new SqlParameter("@offset", offset),
            new SqlParameter("@take", take)), sources, cancellationToken);

    /// <summary>On CMS 13, log rows with the jobs' shown names instead of the class names stored.</summary>
    private static async Task<IReadOnlyList<JobLogRow>> ReadableAsync(CmsDatabase db, IReadOnlyList<JobLogRow> rows, IReadOnlyList<ScheduledJobSource>? sources, CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || (await db.SchemaAsync(cancellationToken)).Major < 13)
        {
            return rows;
        }
        var names = (await ListAsync(db, cancellationToken, sources)).ToDictionary(j => j.Id, j => j.Name);
        return rows.Select(r => names.TryGetValue(r.JobId, out var name) ? r with { JobName = name } : r).ToList();
    }

    /// <summary>The id of the job's latest log row, 0 when it has none: the row a run writes after it has a higher one.</summary>
    public static async Task<long> LatestLogIdAsync(CmsDatabase db, Guid job, CancellationToken cancellationToken) =>
        (await db.QueryAsync(LatestLogIdSql, r => Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), cancellationToken,
            new SqlParameter("@job", System.Data.SqlDbType.UniqueIdentifier) { Value = job })).Single();

    /// <summary>The job's first log row after <paramref name="after"/>; null while there is none.</summary>
    public static async Task<JobLogRow?> LogAfterAsync(CmsDatabase db, Guid job, long after, CancellationToken cancellationToken, IReadOnlyList<ScheduledJobSource>? sources = null) =>
        (await ReadableAsync(db, await db.QueryAsync(LogAfterSql, ReadLog, cancellationToken,
            new SqlParameter("@job", System.Data.SqlDbType.UniqueIdentifier) { Value = job },
            new SqlParameter("@after", System.Data.SqlDbType.BigInt) { Value = after }), sources, cancellationToken)).FirstOrDefault();

    private static JobRow ReadJob(SqlDataReader r) => new(
        r.GetGuid("pkID"),
        r.GetStringOrNull("Name") ?? "",
        r.GetBooleanOrNull("Enabled") ?? false,
        r.GetDateTimeOrNull("LastExec"),
        r.GetInt32OrNull("LastStatus"),
        r.GetStringOrNull("LastText"),
        r.GetDateTimeOrNull("NextExec"),
        NullIfEmpty(r.GetStringOrNull("DatePart")),
        r.GetInt32("Interval"),
        r.GetStringOrNull("TypeName"),
        r.GetStringOrNull("AssemblyName"),
        r.GetBooleanOrNull("IsRunning") ?? false,
        r.GetStringOrNull("CurrentStatusMessage"),
        r.GetInt32OrNull("SecondsSincePing"),
        r.GetBooleanOrNull("IsStoppable") ?? false,
        r.GetBooleanOrNull("Restartable") ?? false,
        r.GetBooleanOrNull("Hidden") ?? false);

    private static JobLogRow ReadLog(SqlDataReader r)
    {
        var duration = r.GetOrdinal("Duration");
        return new JobLogRow(
            Convert.ToInt64(r.GetValue(r.GetOrdinal("pkID")), System.Globalization.CultureInfo.InvariantCulture),
            r.GetGuid("fkScheduledItemId"),
            r.GetStringOrNull("Name"),
            r.GetDateTimeOrNull("Exec") ?? DateTime.MinValue,
            r.IsDBNull(duration) ? null : TimeSpan.FromTicks(Convert.ToInt64(r.GetValue(duration), System.Globalization.CultureInfo.InvariantCulture)),
            r.GetInt32OrNull("Status"),
            r.GetInt32OrNull("Trigger"),
            r.GetStringOrNull("Server"),
            r.GetStringOrNull("Text"));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
