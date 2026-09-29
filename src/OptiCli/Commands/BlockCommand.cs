using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class BlockCommand
{
    public static Command Create(GlobalOptions options)
    {
        var block = new Command("block", "Create shared blocks (subcommand: block create).");
        block.Subcommands.Add(CreateBlock(options));
        return block;
    }

    private static Command CreateBlock(GlobalOptions options)
    {
        var type = new Option<string>("--type") { Description = "Block type name or GUID (see `opticli types --kind block`).", Required = true, HelpName = "type" };
        var name = new Option<string>("--name") { Description = "Block name.", Required = true, HelpName = "name" };
        var forContent = new Option<string?>("--for") { Description = "Put it in this content's \"For this page\" assets folder (created if missing).", HelpName = "ref" };
        var parent = new Option<string?>("--parent") { Description = "Put it in this folder instead.", HelpName = "ref" };
        var lang = new Option<string?>("--lang") { Description = "Language. Default: the master language of --for or --parent.", HelpName = "code" };
        var write = new WriteOptions();
        var command = new Command("create", """
            Create a shared block, as a draft unless --publish. Needs `opticli serve`.
            Give exactly one of --for (the page's own "For this page" assets folder) or --parent (a block folder). Put it on a page
            with `opticli area <page-ref> <Prop> add <block-ref>`.
            Example: opticli block create --type TeaserBlock --name "Teaser" --for 123 Heading=Hi --dry-run
            """);
        command.Options.Add(type);
        command.Options.Add(name);
        command.Options.Add(forContent);
        command.Options.Add(parent);
        command.Options.Add(lang);
        write.AddProperties(command);
        write.AddCommon(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new BlockCreateOperation(
                parse.GetValue(type)!, parse.GetValue(name)!, parse.GetValue(forContent), parse.GetValue(parent),
                write.ParseProperties(context), parse.GetValue(lang), parse.GetValue(write.Publish));
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
