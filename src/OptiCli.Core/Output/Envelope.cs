using OptiCli.Core.Errors;

namespace OptiCli.Core.Output;

/// <summary>
/// The shape of every response: <c>{"ok": true, "data": ..., "meta": {...}}</c> or
/// <c>{"ok": false, "error": {"code", "message", "hint"}}</c>.
/// </summary>
public sealed record Envelope(bool Ok, object? Data, Meta? Meta, ErrorBody? Error)
{
    public static Envelope Success(object? data, Meta meta) => new(true, data, meta, null);

    public static Envelope Failure(ErrorCode code, string message, string? hint = null, object? details = null) =>
        new(false, null, null, new ErrorBody(ExitCodes.Name(code), message, hint, details));
}

/// <param name="Source"><c>db</c> for reads from SQL, <c>agent</c> for calls into the running site.</param>
/// <param name="Version">opticli version that produced the response.</param>
/// <param name="Next">Cursor for the next page (pass as <c>--cursor</c>); null on the last page.</param>
/// <param name="Warnings">Things the caller should know about an otherwise successful result (e.g. a capped search).</param>
/// <param name="Database">Present when the database used is remote, so it is never missed.</param>
public sealed record Meta(string Source, string Version, string? Next = null, IReadOnlyList<string>? Warnings = null, DatabaseMeta? Database = null);

/// <param name="Development">It is the project's development database (chosen by the user); false means opticli was pointed elsewhere for this run.</param>
public sealed record DatabaseMeta(string? Server, string? Name, bool Local, bool Development);

/// <param name="Details">Structured context for some errors (validation issues, log lines, partial plan runs).</param>
public sealed record ErrorBody(string Code, string Message, string? Hint, object? Details = null);
