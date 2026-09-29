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
        var write = new WriteOptions();
        var command = new Command("translate", """
            Create a language branch of existing content, as a draft unless --publish. Needs `opticli serve`.
            Culture-specific properties may be given; shared ones come from the master language and can't be set here.
            Example: opticli translate 123 --lang en --name "About" Heading="About us" --dry-run
            """);
        content.AddTo(command);
        write.AddProperties(command);
        command.Options.Add(name);
        write.AddCommon(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new TranslateOperation(parse.GetValue(content.Ref)!, parse.GetValue(content.Lang)!, parse.GetValue(name), write.ParseProperties(context), parse.GetValue(write.Publish));
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
