using OptiCli.Core.Output;

namespace OptiCli.Cli;

/// <param name="Next">Paging cursor, surfaced as <c>meta.next</c>.</param>
/// <param name="Text">
/// Replaces the generic table rendering in text mode, for shapes tables show badly (a nested tree).
/// JSON output is unaffected.
/// </param>
/// <param name="Warnings">Surfaced as <c>meta.warnings</c>.</param>
/// <param name="Source"><c>meta.source</c>: <c>db</c> for reads, <c>agent</c> for writes done by the running site, <c>cli</c> for local files only.</param>
/// <param name="Raw">Printed as-is in every output mode instead of an envelope (e.g. shell lines meant for <c>eval</c>).</param>
/// <param name="Lines">The list that <c>--jsonl</c> writes, when it is not <paramref name="Data"/> itself (e.g. the rows of a SQL result).</param>
internal sealed record CommandResult(
    object? Data,
    string? Next = null,
    string? Text = null,
    IReadOnlyList<string>? Warnings = null,
    string Source = CommandResult.DbSource,
    string? Raw = null,
    object? Lines = null)
{
    public const string DbSource = "db";

    public const string CliSource = "cli";

    public static CommandResult From<T>(Page<T> page) => new(page.Items, page.Next);
}
