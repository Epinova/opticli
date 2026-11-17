using OptiCli.Core.Output;

namespace OptiCli.Cli;

/// <summary>
/// What a command returns, whatever its data: the runner writes the envelope from it. <see cref="CommandResult{T}"/> is
/// the only kind there is, so every command declares the type of its <c>data</c> (<see cref="CommandRunner.SetHandler{T}"/>);
/// this base carries what doesn't depend on it, and the data boxed, which is serialized by its runtime type as before.
/// </summary>
/// <param name="Next">Paging cursor, surfaced as <c>meta.next</c>.</param>
/// <param name="Text">
/// Replaces the generic table rendering in text mode, for shapes tables show badly (a nested tree).
/// JSON output is unaffected.
/// </param>
/// <param name="Warnings">Surfaced as <c>meta.warnings</c>.</param>
/// <param name="Source"><c>meta.source</c>: <c>db</c> for reads, <c>agent</c> for writes done by the running site, <c>cli</c> for local files only.</param>
/// <param name="Raw">Printed as-is in every output mode instead of an envelope (e.g. shell lines meant for <c>eval</c>).</param>
/// <param name="Lines">The list that <c>--jsonl</c> writes, when it is not the data itself (e.g. the rows of a SQL result).</param>
internal abstract record CommandResult(
    string? Next,
    string? Text,
    IReadOnlyList<string>? Warnings,
    string Source,
    string? Raw,
    object? Lines)
{
    public const string DbSource = "db";

    public const string CliSource = "cli";

    /// <summary>The data, boxed: what the envelope's <c>data</c> is written from.</summary>
    public abstract object Untyped { get; }

    public static CommandResult<IReadOnlyList<T>> From<T>(Page<T> page) where T : notnull => new(page.Items, page.Next);
}

/// <summary>A command's result, with <see cref="Data"/> of the type the command declares (the schema reports it).</summary>
internal sealed record CommandResult<T>(
    T Data,
    string? Next = null,
    string? Text = null,
    IReadOnlyList<string>? Warnings = null,
    string Source = CommandResult.DbSource,
    string? Raw = null,
    object? Lines = null) : CommandResult(Next, Text, Warnings, Source, Raw, Lines)
    where T : notnull
{
    public override object Untyped => Data;
}

/// <summary>
/// The data of a command that only ever prints <see cref="CommandResult.Raw"/> (<c>skill print</c>): there is no
/// envelope, so nothing to describe. The schema leaves such a command's result out.
/// </summary>
internal sealed record RawOutput
{
    public static readonly RawOutput Instance = new();
}
