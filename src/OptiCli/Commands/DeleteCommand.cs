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
        var ignoreReferences = new Option<bool>("--ignore-references")
        {
            Description = "Delete it even when other content references it or its descendants. Without it such a delete asks on a terminal, and elsewhere fails with a conflict (exit 5) that lists the references.",
        };
        var command = new Command("delete", """
            Move content, with everything below it, to the recycle bin. Needs `opticli serve`. Nothing is deleted permanently.
            Undo with `opticli restore <ref>` (`opticli trash` lists the recycle bin). Prints what moved, including how many descendants, and
            references from other content to any of it (references, referenceCount): those would point into the recycle
            bin, so a delete with references needs --ignore-references. --dry-run shows all that without asking the site.
            Example: opticli delete 123 --dry-run
            """);
        content.AddTo(command, withLang: false);
        write.AddCommon(command, publish: false);
        command.Options.Add(ignoreReferences);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new DeleteOperation(parse.GetValue(content.Ref)!, parse.GetValue(ignoreReferences));
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
