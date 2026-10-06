using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Jobs;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;
using OptiCli.Core.Refs;

namespace OptiCli.Commands;

internal static class HistoryCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var since = new Option<string?>("--since") { Description = $"Only changes since: {JobTimes.SinceSyntax}.", HelpName = "date|age" };
        var by = new Option<string?>("--by") { Description = "Only changes by a user whose name contains this.", HelpName = "user" };
        var list = new ListOptions(options);
        var command = new Command("history", """
            What the CMS's change log (tblActivityLog) says happened to one content item, newest first: when (UTC), by whom,
            action (create, publish, delayedPublish, requestApproval, rejected, checkIn, move, delete = moved to the recycle
            bin, restore = moved out of it, deletePermanently, deleteLanguage, deleteVersion, ...), the version it was about,
            language and name, from and to for moves, and previousStatus for a publish. It is the only record of moves and
            deletes. Drafts saved aren't in it (`opticli versions <ref>` lists every version), and the CMS's Change Log Auto
            Truncate job removes old entries. Read from the database; nothing needs to run.
            Example: opticli history 123 --since 30d
            """);
        content.AddTo(command, withLang: false);
        command.Options.Add(since);
        command.Options.Add(by);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var from = parse.GetValue(since) is { } text ? JobTimes.ParseSince(text, DateTime.UtcNow) : (DateTime?)null;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var reference = parse.GetValue(content.Ref)!;
            // A content provider's item isn't in tblContent, but the change log names it all the same.
            var parsed = ContentRefParser.Parse(reference);
            var (id, provider) = parsed.Kind == ContentRefKind.Provider
                ? (parsed.Id, parsed.Provider)
                : ((await session.LocateAsync(reference, parse.GetValue(content.Site), cancellationToken)).Id, null);
            var (offset, limit) = list.Window(parse);
            var entries = await new HistoryReader(session).ListAsync(id, provider, from, parse.GetValue(by), offset, limit, cancellationToken);
            var page = Paging.FromWindow(entries, offset, limit);
            var warnings = offset == 0 && page.Items.Count == 0
                ? [$"The change log has nothing for {reference}{(from is null && parse.GetValue(by) is null ? "" : " that matches")}: its entries may be older than what the Change Log Auto Truncate job keeps. `opticli versions {reference}` lists its versions."]
                : (List<string>?)null;
            return new CommandResult(page.Items, page.Next, Warnings: warnings);
        });
        return command;
    }
}
