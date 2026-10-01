using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class MoveCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var to = new Option<string>("--to") { Description = "New parent (any ref).", Required = true, HelpName = "parent-ref" };
        var write = new WriteOptions();
        var command = new Command("move", """
            Move content, with everything below it, under a new parent. Needs `opticli serve`.
            Site roots, start pages and the recycle bin can't be moved; `opticli delete` moves to the recycle bin. The content's
            type must be allowed below the new parent, as for `create`. Prints the new and previous parent and how many
            descendants moved along. --dry-run runs every check, the site's included, without moving.
            Example: opticli move 123 --to 45 --dry-run
            """);
        content.AddTo(command, withLang: false);
        command.Options.Add(to);
        write.AddCommon(command, publish: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new MoveOperation(parse.GetValue(content.Ref)!, parse.GetValue(to)!);
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
