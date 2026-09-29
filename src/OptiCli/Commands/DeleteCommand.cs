using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class DeleteCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var write = new WriteOptions();
        var command = new Command("delete", """
            Move content, with everything below it, to the recycle bin. Needs `opticli serve`. Nothing is deleted permanently.
            Undo with `opticli move <ref> --to <previousParent>`. Prints what moved, including how many descendants.
            --dry-run shows that without asking the site.
            Example: opticli delete 123 --dry-run
            """);
        content.AddTo(command, withLang: false);
        write.AddCommon(command, publish: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new DeleteOperation(parse.GetValue(content.Ref)!);
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
