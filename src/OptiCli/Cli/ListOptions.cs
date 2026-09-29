using System.CommandLine;
using OptiCli.Core.Output;

namespace OptiCli.Cli;

/// <summary><c>--limit</c> / <c>--cursor</c> / <c>--jsonl</c> for commands that return lists.</summary>
internal sealed class ListOptions(GlobalOptions options)
{
    public Option<int?> Limit { get; } = new("--limit")
    {
        Description = $"Maximum items per page (default {Paging.DefaultLimit}).",
        HelpName = "n",
    };

    public Option<string?> Cursor { get; } = new("--cursor")
    {
        Description = "Continue from a previous page: pass meta.next.",
        HelpName = "cursor",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Limit);
        command.Options.Add(Cursor);
        options.AddJsonLines(command);
    }

    public Page<T> Apply<T>(ParseResult parse, IReadOnlyList<T> items) =>
        Paging.Apply(items, parse.GetValue(Limit), parse.GetValue(Cursor));

    /// <summary>Offset and size for lists paged in SQL (see <see cref="Paging.Window"/>).</summary>
    public (int Offset, int Limit) Window(ParseResult parse) => Paging.Window(parse.GetValue(Limit), parse.GetValue(Cursor));
}
