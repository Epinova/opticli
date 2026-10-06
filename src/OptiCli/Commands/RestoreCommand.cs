using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class RestoreCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var to = new Option<string?>("--to")
        {
            Description = "Restore it below this parent (any ref) instead of where it was before it was deleted. Needed when the CMS has no record of that (trash shows originalParent: null).",
            HelpName = "parent-ref",
        };
        var write = new WriteOptions();
        var command = new Command("restore", """
            Bring content back out of the recycle bin, with everything below it, as the edit UI's Restore does: below the
            parent it had before it was deleted (the CMS stores it; `opticli trash` shows it as originalParent), or below --to.
            Needs `opticli serve`. The item must be what was deleted, directly in the recycle bin: restoring something below it
            is a usage error naming the item to restore instead. Its type must be allowed below the parent, as for `move`;
            a parent in the recycle bin too is a conflict (restore that first, or --to). It keeps its versions, so content
            that was published is live again. Undo with `opticli delete <ref>`. --dry-run runs every check, the site's
            included, without moving it.
            Example: opticli restore 123 --dry-run
            Example: opticli restore 123 --to 45
            """);
        content.AddTo(command, withLang: false);
        command.Options.Add(to);
        write.AddCommon(command, publish: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new RestoreOperation(parse.GetValue(content.Ref)!, parse.GetValue(to));
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
