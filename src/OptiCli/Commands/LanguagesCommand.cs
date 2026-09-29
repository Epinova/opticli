using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;

namespace OptiCli.Commands;

internal static class LanguagesCommand
{
    public static Command Create(GlobalOptions options)
    {
        var all = new Option<bool>("--all") { Description = "Include disabled language branches." };
        var list = new ListOptions(options);
        var command = new Command("languages", """
            List language branches: code, name, which sites use it as master language, and how many content items it has.
            Disabled branches are only listed with --all.
            Example: opticli languages
            """);
        command.Options.Add(all);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var languages = await LanguageReader.ListAsync(db, cancellationToken);
            var shown = context.Parse.GetValue(all) ? languages : languages.Where(l => l.Enabled).ToList();
            return CommandResult.From(list.Apply(context.Parse, shown));
        });
        return command;
    }
}
