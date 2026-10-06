using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class CategoriesCommand
{
    public static Command Create(GlobalOptions options)
    {
        var list = new ListOptions(options);
        var command = new Command("categories", """
            List the site's categories (admin mode's category tree), depth first: id, name (what `set Category=...` and Category
            properties take, case doesn't matter; `get` shows these names), description (the name edit mode shows, which set
            takes too), parent, path, depth, selectable (only those can be set), visible (shown in edit mode) and items (how
            many content items have it). Read from the database; nothing needs to run.
            Example: opticli categories
            """);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            var categories = await new CategoryReader(db).ListAsync(cancellationToken);
            return CommandResult.From(list.Apply(context.Parse, categories));
        });
        return command;
    }
}
