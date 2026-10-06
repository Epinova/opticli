namespace OptiCli.Core.Errors;

/// <summary>
/// An expected failure with a stable code and a hint telling the caller what to do next.
/// Anything thrown that is not an <see cref="OptiCliException"/> is reported as an internal error.
/// </summary>
public abstract class OptiCliException(ErrorCode code, string message, string? hint = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ErrorCode Code { get; } = code;

    public string? Hint { get; } = hint;

    /// <summary>Structured detail for the error envelope's <c>details</c> (validation issues, log lines, a partial plan run).</summary>
    public object? Details { get; init; }

    /// <summary>The exception type for <paramref name="code"/>, e.g. to re-raise an error with more context.</summary>
    public static OptiCliException Create(ErrorCode code, string message, string? hint = null, object? details = null) => code switch
    {
        ErrorCode.Usage => new UsageException(message, hint) { Details = details },
        ErrorCode.NotFound => new NotFoundException(message, hint) { Details = details },
        ErrorCode.Refused => new RefusedException(message, hint) { Details = details },
        ErrorCode.Unreachable => new UnreachableException(message, hint) { Details = details },
        ErrorCode.Conflict => new ConflictException(message, hint) { Details = details },
        ErrorCode.Validation => new ContentValidationException(message, hint) { Details = details },
        ErrorCode.NeedsSelection => new NeedsSelectionException(message, hint) { Details = details },
        ErrorCode.Cancelled => new CancelledException(message, hint) { Details = details },
        ErrorCode.Drift => new DriftException(message, hint) { Details = details },
        ErrorCode.JobFailed => new JobFailedException(message, hint) { Details = details },
        ErrorCode.Timeout => new TimedOutException(message, hint) { Details = details },
        _ => new InternalException(message, hint) { Details = details },
    };
}

/// <summary>Bad arguments, ambiguous input, or a request the tool cannot interpret.</summary>
public sealed class UsageException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Usage, message, hint);

/// <summary>The project, connection string, content item or type does not exist.</summary>
public sealed class NotFoundException(string message, string? hint = null)
    : OptiCliException(ErrorCode.NotFound, message, hint);

/// <summary>A safety rule blocked the operation (for example serving a site against a shared database that isn't the development one).</summary>
public sealed class RefusedException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Refused, message, hint);

/// <summary>The database or the site's agent could not be reached.</summary>
public sealed class UnreachableException(string message, string? hint = null, Exception? inner = null)
    : OptiCliException(ErrorCode.Unreachable, message, hint, inner);

/// <summary>A write conflicted with a newer version, or doesn't apply to the content's current state.</summary>
public sealed class ConflictException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Conflict, message, hint);

/// <summary>The CMS's validation rejected a write; <see cref="OptiCliException.Details"/> lists every issue.</summary>
public sealed class ContentValidationException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Validation, message, hint);

/// <summary>
/// The user has to choose first, e.g. which database is the project's development database. Details list the
/// choices; an agent relays them to the user instead of choosing.
/// </summary>
public sealed class NeedsSelectionException(string message, string? hint = null)
    : OptiCliException(ErrorCode.NeedsSelection, message, hint);

/// <summary>
/// A write against a shared database whose content model differs from the local build's, which the user hasn't confirmed.
/// <see cref="OptiCliException.Details"/> is the drift report; the user confirms its fingerprint.
/// </summary>
public sealed class DriftException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Drift, message, hint);

/// <summary><c>jobs run</c>: the job ran and didn't succeed; <see cref="OptiCliException.Details"/> is the run, with its log entry.</summary>
public sealed class JobFailedException(string message, string? hint = null)
    : OptiCliException(ErrorCode.JobFailed, message, hint);

/// <summary>Waiting ran out of time while the work goes on (a job still runs after <c>jobs run --timeout</c>).</summary>
public sealed class TimedOutException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Timeout, message, hint);

/// <summary>The user interrupted the command (Ctrl+C).</summary>
public sealed class CancelledException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Cancelled, message, hint);

/// <summary>Something unexpected failed, e.g. inside the site.</summary>
public sealed class InternalException(string message, string? hint = null)
    : OptiCliException(ErrorCode.Internal, message, hint);
