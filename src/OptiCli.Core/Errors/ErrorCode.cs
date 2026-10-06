namespace OptiCli.Core.Errors;

/// <summary>
/// Machine-readable error codes. Each maps to one process exit code, so scripts can branch on
/// either the envelope's <c>error.code</c> or <c>$?</c>.
/// </summary>
public enum ErrorCode
{
    Usage,
    NotFound,
    Refused,
    Unreachable,
    Conflict,

    /// <summary>The CMS's validation rejected a write (or a dry run found it would).</summary>
    Validation,

    /// <summary>opticli needs the user to choose something first (the project's development database).</summary>
    NeedsSelection,
    Internal,

    /// <summary>Interrupted (Ctrl+C) while it ran; a plan reports what it saved before that.</summary>
    Cancelled,

    /// <summary>A write in shared mode while the local build and the database differ, which the user hasn't confirmed.</summary>
    Drift,

    /// <summary><c>jobs run</c>: the job ran and didn't succeed (failed, couldn't start, was stopped or aborted).</summary>
    JobFailed,

    /// <summary>Waiting for something that is still under way ran out of time (<c>jobs run --timeout</c>); it goes on.</summary>
    Timeout,
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int NotFound = 2;
    public const int Refused = 3;
    public const int Unreachable = 4;
    public const int Conflict = 5;
    public const int NeedsSelection = 6;

    /// <summary><c>jobs run</c>: the job ran and failed (6 is <see cref="NeedsSelection"/>'s).</summary>
    public const int JobFailed = 7;

    /// <summary>128 + SIGINT, as shells report a process stopped by Ctrl+C.</summary>
    public const int Cancelled = 130;

    public static int For(ErrorCode code) => code switch
    {
        ErrorCode.Usage => Usage,
        ErrorCode.NotFound => NotFound,
        ErrorCode.Refused => Refused,
        ErrorCode.Unreachable or ErrorCode.Timeout => Unreachable,
        ErrorCode.Conflict or ErrorCode.Validation or ErrorCode.Drift => Conflict,
        ErrorCode.NeedsSelection => NeedsSelection,
        ErrorCode.JobFailed => JobFailed,
        ErrorCode.Cancelled => Cancelled,
        // Unexpected failures share the usage code: the spec reserves 2-5 for specific outcomes.
        _ => Usage,
    };

    /// <summary>The snake_case name used in the JSON envelope.</summary>
    public static string Name(ErrorCode code) => code switch
    {
        ErrorCode.Usage => "usage",
        ErrorCode.NotFound => "not_found",
        ErrorCode.Refused => "refused",
        ErrorCode.Unreachable => "unreachable",
        ErrorCode.Conflict => "conflict",
        ErrorCode.Validation => "validation",
        ErrorCode.NeedsSelection => "needs_selection",
        ErrorCode.Cancelled => "cancelled",
        ErrorCode.Drift => "drift",
        ErrorCode.JobFailed => "job_failed",
        ErrorCode.Timeout => "timeout",
        _ => "internal",
    };
}
