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
            Commerce's catalog content too, into a language of its catalog (code:P-1001 --lang sv DisplayName=...): where
            Commerce has the language already (Commerce 14 always), it is saved into it as set --lang does.
            Example: opticli translate 123 --lang en --name "About" Heading="About us" --dry-run
            """);
        content.AddTo(command);
        write.AddProperties(command);
        command.Options.Add(name);
        command.Options.Add(withBlocks);
        command.Options.Add(remove);
        command.Options.Add(confirm);
        write.AddCommon(command);
        write.AddProject(command);

        // The operation as the options give it; --in-project and --ready are checked with the other options, before the
        // database is opened.
        TranslateOperation Operation(CliContext context)
        {
            var parse = context.Parse;
            var operation = new TranslateOperation(parse.GetValue(content.Ref)!, parse.GetValue(content.Lang)!, parse.GetValue(name), write.ParseProperties(context), parse.GetValue(write.Publish))
            {
                WithBlocks = parse.GetValue(withBlocks),
                Remove = parse.GetValue(remove),
                Confirm = parse.GetValue(confirm),
            };
            return write.WithApproval(operation, parse);
        }
        CommandRunner.SetHandler(command, options, new ResultModes()
            .When(ModeCondition.Set(remove), async (context, cancellationToken) =>
            {
                var operation = Operation(context);
                await using var session = await context.OpenContentAsync(cancellationToken);
                return WriteOptions.Result(await context.Writes(session, context.Parse.GetValue(content.Site)).RemoveLanguageAsync(operation, context.Parse.GetValue(write.DryRun), cancellationToken));
            })
            .Otherwise(async (context, cancellationToken) =>
            {
                var operation = Operation(context);
                await using var session = await context.OpenContentAsync(cancellationToken);
                return WriteOptions.Result(await context.Writes(session, context.Parse.GetValue(content.Site)).TranslateAsync(operation, context.Parse.GetValue(write.DryRun), cancellationToken));
            }));
        return command;
    }
}
