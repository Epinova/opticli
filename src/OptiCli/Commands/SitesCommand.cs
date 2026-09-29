using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;

namespace OptiCli.Commands;

internal static class SitesCommand
{
    public static Command Create(GlobalOptions options)
    {
        var list = new ListOptions(options);
        var command = new Command("sites", """
            List site definitions: name, URL, start page, master language and host names.
            Example: opticli sites
            """);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var sites = await SiteReader.ListAsync(db, cancellationToken);
            return CommandResult.From(list.Apply(context.Parse, sites));
        });
        return command;
    }
}
