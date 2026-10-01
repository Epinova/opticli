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

    /// <summary>128 + SIGINT, as shells report a process stopped by Ctrl+C.</summary>
    public const int Cancelled = 130;

    public static int For(ErrorCode code) => code switch
    {
        ErrorCode.Usage => Usage,
        ErrorCode.NotFound => NotFound,
        ErrorCode.Refused => Refused,
        ErrorCode.Unreachable => Unreachable,
        ErrorCode.Conflict or ErrorCode.Validation => Conflict,
        ErrorCode.NeedsSelection => NeedsSelection,
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
        _ => "internal",
    };
}
