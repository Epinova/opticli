using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class TranslateCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        content.Lang.Description = "Language of the new branch (must be enabled on the site, see `opticli languages`).";
        content.Lang.Required = true;
        var name = new Option<string?>("--name") { Description = "Name in the new language. Default: the master language's name.", HelpName = "name" };
        var withBlocks = new Option<bool>("--with-blocks")
        {
            Description = "Also give every block in its \"For this page\" folder the branch (a copy of the block's master language), published with --publish, so the new branch doesn't show blocks in another language. Output: blocks.",
        };
        var remove = new Option<bool>("--remove") { Description = "Delete the branch with all its versions instead. Not the master language, nor a site's start page. Can't be undone." };
        var confirm = new Option<bool>("--confirm") { Description = "Confirms --remove. Without it the removal asks on a terminal, and elsewhere fails with a conflict (exit 5)." };
        var write = new WriteOptions();
        var command = new Command("translate", """
            Create a language branch of existing content, as a draft unless --publish. Needs `opticli serve`.
            Culture-specific properties may be given; shared ones come from the master language and can't be set here.
            --with-blocks translates the blocks in its "For this page" folder too. --remove deletes a branch instead.
            Example: opticli translate 123 --lang en --name "About" Heading="About us" --dry-run
            """);
        content.AddTo(command);
        write.AddProperties(command);
        command.Options.Add(name);
        command.Options.Add(withBlocks);
        command.Options.Add(remove);
        command.Options.Add(confirm);
        write.AddCommon(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new TranslateOperation(parse.GetValue(content.Ref)!, parse.GetValue(content.Lang)!, parse.GetValue(name), write.ParseProperties(context), parse.GetValue(write.Publish))
            {
                WithBlocks = parse.GetValue(withBlocks),
                Remove = parse.GetValue(remove),
                Confirm = parse.GetValue(confirm),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(write.WithApproval(operation, parse), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
