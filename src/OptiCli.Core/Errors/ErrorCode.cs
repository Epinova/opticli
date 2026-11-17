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

    /// <summary>
    /// The database server answered but refused the login: no user for the identity in the database, a wrong password,
    /// or a database that doesn't exist (exit 4, as <see cref="Unreachable"/>).
    /// </summary>
    LoginFailed,
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
        ErrorCode.Unreachable or ErrorCode.LoginFailed or ErrorCode.Timeout => Unreachable,
        ErrorCode.Conflict or ErrorCode.Validation or ErrorCode.Drift => Conflict,
        ErrorCode.NeedsSelection => NeedsSelection,
        ErrorCode.JobFailed => JobFailed,
        ErrorCode.Cancelled => Cancelled,
        // Unexpected failures share the usage code: the spec reserves 2-5 for specific outcomes.
        _ => Usage,
    };

    /// <summary>The snake_case name used in the JSON envelope (<see cref="ErrorCodeNames"/>).</summary>
    public static string Name(ErrorCode code) => code switch
    {
        ErrorCode.Usage => ErrorCodeNames.Usage,
        ErrorCode.NotFound => ErrorCodeNames.NotFound,
        ErrorCode.Refused => ErrorCodeNames.Refused,
        ErrorCode.Unreachable => ErrorCodeNames.Unreachable,
        ErrorCode.LoginFailed => ErrorCodeNames.LoginFailed,
        ErrorCode.Conflict => ErrorCodeNames.Conflict,
        ErrorCode.Validation => ErrorCodeNames.Validation,
        ErrorCode.NeedsSelection => ErrorCodeNames.NeedsSelection,
        ErrorCode.Cancelled => ErrorCodeNames.Cancelled,
        ErrorCode.Drift => ErrorCodeNames.Drift,
        ErrorCode.JobFailed => ErrorCodeNames.JobFailed,
        ErrorCode.Timeout => ErrorCodeNames.Timeout,
        _ => ErrorCodeNames.Internal,
    };
}

/// <summary>The envelope's <c>error.code</c> values: <see cref="ExitCodes.Name"/> of each <see cref="ErrorCode"/>.</summary>
[Protocol.ValueSetName("ErrorCode")]
public static class ErrorCodeNames
{
    public const string Usage = "usage";
    public const string Internal = "internal";
    public const string NotFound = "not_found";
    public const string Refused = "refused";
    public const string Unreachable = "unreachable";
    public const string LoginFailed = "login_failed";
    public const string Timeout = "timeout";
    public const string Conflict = "conflict";
    public const string Validation = "validation";
    public const string Drift = "drift";
    public const string NeedsSelection = "needs_selection";
    public const string JobFailed = "job_failed";
    public const string Cancelled = "cancelled";
}
